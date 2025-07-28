using Content.Shared.AU14;
using Robust.Shared.Prototypes;
using System.Collections.Generic;
using Content.Shared._RMC14.Requisitions;
using Content.Shared._RMC14.Requisitions.Components;

namespace Content.Shared.AU14;
    [Prototype]
    public sealed partial class PlatoonPrototype : IPrototype
    {
        [IdDataField]
        public string ID { get; private set; } = default!;

        [DataField("name", required: true)]
        public string Name { get; private set; } = string.Empty;

        [DataField("VendorToMarker")]
        public Dictionary<PlatoonMarkerClass, ProtoId<EntityPrototype>> VendorMarkersByClass { get; private set; } = new();

       // [DataField("ship")]
        //public ProtoId<GameMapPrototype>? GameMap;

      //  [DataField("language")]
       // public ProtoId<LanguagePrototype> Language { get; private set; } = default!;

        [DataField("logilist")]
        public RequisitionsComputerComponent Logilist { get; private set; } = default!;

    }
