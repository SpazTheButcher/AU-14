using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._CMU14.Dropship.Integrity;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared._RMC14.Repairable;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Tools;
using Robust.Shared.Prototypes;

namespace Content.Server._CMU14.Dropship.Integrity;

public sealed partial class DropshipIntegritySystem
{
    private readonly record struct MalfunctionRepairStep(
        ProtoId<ToolQualityPrototype> Tool,
        float Time,
        string Instruction,
        string ToolName,
        bool RequiresWelder = false);

    private static readonly IReadOnlyList<MalfunctionRepairStep> WeaponShortRepair =
    [
        new("Screwing", 4f, "Open the fire-control panel.", "screwdriver"),
        new("Cutting", 5f, "Cut out the burned wiring.", "wirecutters"),
        new("Pulsing", 6f, "Reset the fire-control relay.", "multitool"),
        new("Screwing", 4f, "Close the fire-control panel.", "screwdriver"),
    ];

    private static readonly IReadOnlyList<MalfunctionRepairStep> PropulsionFaultRepair =
    [
        new("Screwing", 4f, "Open the propulsion service panel.", "screwdriver"),
        new("Pulsing", 6f, "Diagnose and reset the propulsion controller.", "multitool"),
        new("Anchoring", 5f, "Tighten the engine mounts.", "wrench"),
    ];

    private static readonly IReadOnlyList<MalfunctionRepairStep> ManeuveringThrusterRepair =
    [
        new("Prying", 5f, "Free the jammed thruster actuator.", "crowbar"),
        new("Anchoring", 6f, "Realign and tighten the thruster assembly.", "wrench"),
        new("Welding", 8f, "Patch the cracked thrust duct.", "welder", true),
    ];

    private static readonly IReadOnlyList<MalfunctionRepairStep> SensorArrayRepair =
    [
        new("Screwing", 4f, "Open the sensor housing.", "screwdriver"),
        new("Cutting", 5f, "Remove the damaged data lead.", "wirecutters"),
        new("Pulsing", 6f, "Recalibrate the sensor array.", "multitool"),
        new("Screwing", 4f, "Close the sensor housing.", "screwdriver"),
    ];

    private void TryTriggerMalfunctions(Entity<DropshipIntegrityComponent> dropship, float previousIntegrity)
    {
        if (dropship.Comp.MaxIntegrity <= 0f || dropship.Comp.Integrity <= 0f)
            return;

        var previousRatio = previousIntegrity / dropship.Comp.MaxIntegrity;
        var currentRatio = dropship.Comp.Integrity / dropship.Comp.MaxIntegrity;
        for (var i = 0; i < dropship.Comp.MalfunctionThresholds.Length; i++)
        {
            var thresholdMask = 1 << i;
            if ((dropship.Comp.TriggeredMalfunctionThresholds & thresholdMask) != 0)
                continue;

            var threshold = dropship.Comp.MalfunctionThresholds[i];
            if (previousRatio > threshold && currentRatio <= threshold)
            {
                dropship.Comp.TriggeredMalfunctionThresholds |= thresholdMask;
                TryAddRandomMalfunction(dropship);
            }
        }
    }

    private void TryAddRandomMalfunction(Entity<DropshipIntegrityComponent> dropship)
    {
        if (dropship.Comp.ActiveMalfunctions.Count >= dropship.Comp.MaxActiveMalfunctions)
            return;

        var available = Enum.GetValues<DropshipMalfunction>()
            .Where(malfunction => !dropship.Comp.ActiveMalfunctions.Contains(malfunction))
            .ToList();
        if (available.Count == 0)
            return;

        var malfunction = available[_random.Next(available.Count)];
        dropship.Comp.ActiveMalfunctions.Add(malfunction);
        dropship.Comp.MalfunctionRepairProgress.Remove(malfunction);
        dropship.Comp.RepairingMalfunctions.Remove(malfunction);
        Dirty(dropship);

        var message = $"{DropshipMalfunctionData.GetAlertName(malfunction)} detected.";
        var hudQuery = EntityQueryEnumerator<GunshipPilotHudComponent>();
        while (hudQuery.MoveNext(out var pilot, out var hud))
        {
            if (hud.Dropship == dropship.Owner)
                _popup.PopupEntity(message, dropship.Owner, pilot, PopupType.LargeCaution);
        }
    }

    private bool TryStartMalfunctionRepair(
        Entity<DropshipHullComponent> target,
        Entity<DropshipIntegrityComponent> integrity,
        ref InteractUsingEvent args)
    {
        if (integrity.Comp.ActiveMalfunctions.Count == 0)
            return false;

        if (integrity.Comp.Wrecked || integrity.Comp.Crashing)
        {
            args.Handled = true;
            _popup.PopupEntity("This dropship is wrecked beyond repair.", target, args.User, PopupType.SmallCaution);
            return true;
        }

        foreach (var malfunction in integrity.Comp.ActiveMalfunctions)
        {
            if (integrity.Comp.RepairingMalfunctions.Contains(malfunction))
            {
                args.Handled = true;
                return true;
            }

            var stepIndex = GetRepairProgress(integrity.Comp, malfunction);
            var steps = GetRepairSteps(malfunction);
            if (stepIndex >= steps.Count)
                continue;

            var step = steps[stepIndex];
            if (!_tool.HasQuality(args.Used, step.Tool))
                continue;

            args.Handled = true;
            if (step.RequiresWelder &&
                (!HasComp<BlowtorchComponent>(args.Used) ||
                 !_repairable.UseFuel(args.Used, args.User, integrity.Comp.RepairFuel, true)))
            {
                return true;
            }

            integrity.Comp.RepairingMalfunctions.Add(malfunction);
            var doAfter = new DoAfterArgs(EntityManager,
                args.User,
                TimeSpan.FromSeconds(step.Time),
                new DropshipMalfunctionRepairDoAfterEvent(malfunction, stepIndex),
                target,
                target,
                used: args.Used)
            {
                NeedHand = true,
                BreakOnMove = true,
                BreakOnDamage = true,
                BlockDuplicate = true,
                DuplicateCondition = DuplicateConditions.SameEvent,
            };

            if (!_doAfter.TryStartDoAfter(doAfter))
            {
                integrity.Comp.RepairingMalfunctions.Remove(malfunction);
                return true;
            }

            _popup.PopupEntity($"You begin to repair the {DropshipMalfunctionData.GetAlertName(malfunction).ToLowerInvariant()}. {step.Instruction}",
                target,
                args.User);
            return true;
        }

        return false;
    }

    private void OnMalfunctionRepairDoAfter(
        Entity<DropshipHullComponent> target,
        ref DropshipMalfunctionRepairDoAfterEvent args)
    {
        if (!TryGetDropship(target.Owner, out var integrity))
            return;

        integrity.Comp.RepairingMalfunctions.Remove(args.Malfunction);
        if (args.Cancelled || args.Handled || args.Used is not { } used ||
            integrity.Comp.Wrecked || integrity.Comp.Crashing ||
            !integrity.Comp.ActiveMalfunctions.Contains(args.Malfunction))
        {
            return;
        }

        var stepIndex = GetRepairProgress(integrity.Comp, args.Malfunction);
        var steps = GetRepairSteps(args.Malfunction);
        if (args.Step != stepIndex || stepIndex >= steps.Count)
            return;

        var step = steps[stepIndex];
        if (!_tool.HasQuality(used, step.Tool))
            return;

        if (step.RequiresWelder &&
            (!HasComp<BlowtorchComponent>(used) ||
             !_repairable.UseFuel(used, args.User, integrity.Comp.RepairFuel)))
        {
            return;
        }

        args.Handled = true;
        var nextStep = stepIndex + 1;
        if (nextStep < steps.Count)
        {
            integrity.Comp.MalfunctionRepairProgress[args.Malfunction] = nextStep;
            var next = steps[nextStep];
            _popup.PopupEntity($"Repair step complete. Next: {next.Instruction} Use a {next.ToolName}.",
                target,
                args.User);
            return;
        }

        integrity.Comp.ActiveMalfunctions.Remove(args.Malfunction);
        integrity.Comp.MalfunctionRepairProgress.Remove(args.Malfunction);
        Dirty(integrity);
        _popup.PopupEntity($"{DropshipMalfunctionData.GetAlertName(args.Malfunction)} repaired.",
            target,
            args.User,
            PopupType.Medium);
    }

    private void PushMalfunctionDiagnostics(Entity<DropshipIntegrityComponent> integrity, ExaminedEvent args)
    {
        if (integrity.Comp.ActiveMalfunctions.Count == 0)
            return;

        args.PushMarkup("[color=#ff5a36]Dropship malfunctions:[/color]");
        foreach (var malfunction in integrity.Comp.ActiveMalfunctions)
        {
            var stepIndex = GetRepairProgress(integrity.Comp, malfunction);
            var steps = GetRepairSteps(malfunction);
            if (stepIndex >= steps.Count)
                continue;

            var step = steps[stepIndex];
            args.PushMarkup($"[color=#ff5a36]{DropshipMalfunctionData.GetAlertName(malfunction)} detected.[/color] {GetMalfunctionEffect(malfunction)}");
            args.PushMarkup($"Repair {stepIndex + 1}/{steps.Count}: {step.Instruction} Use a {step.ToolName}.");
        }
    }

    private static int GetRepairProgress(DropshipIntegrityComponent integrity, DropshipMalfunction malfunction)
    {
        return integrity.MalfunctionRepairProgress.GetValueOrDefault(malfunction);
    }

    private static IReadOnlyList<MalfunctionRepairStep> GetRepairSteps(DropshipMalfunction malfunction)
    {
        return malfunction switch
        {
            DropshipMalfunction.WeaponShort => WeaponShortRepair,
            DropshipMalfunction.PropulsionFault => PropulsionFaultRepair,
            DropshipMalfunction.ManeuveringThrusterFault => ManeuveringThrusterRepair,
            DropshipMalfunction.SensorArrayFault => SensorArrayRepair,
            _ => Array.Empty<MalfunctionRepairStep>(),
        };
    }

    private static string GetMalfunctionEffect(DropshipMalfunction malfunction)
    {
        return malfunction switch
        {
            DropshipMalfunction.WeaponShort => "The direct-fire weapon is offline.",
            DropshipMalfunction.PropulsionFault => "Forward thrust and maximum speed are reduced.",
            DropshipMalfunction.ManeuveringThrusterFault => "Lateral and rotational response are reduced.",
            DropshipMalfunction.SensorArrayFault => "Upper, lower, and rear cameras are offline.",
            _ => "Its effect is unknown.",
        };
    }
}
