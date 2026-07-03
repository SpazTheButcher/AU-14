using Robust.Client.UserInterface.Controls;

namespace Content.Client._RMC14.Announce.Effects;

public sealed class TitleAssaultScrollEffect : IAnnouncementVisualEffect
{
    public void Apply(AnnouncementEffectContext context, TimeSpan currentTime)
    {
        var titleLabels = context.Output.TitleLabels;
        if (titleLabels.Length < 2)
            return;

        var contentWidth = context.Output.TitleContentWidth;
        var gap = context.Output.TitleScrollGap;
        var period = contentWidth + gap;
        if (period <= 0f)
            return;

        var speed = context.Style.TitleConfig.Effect.Speed;
        var elapsed = (float)(currentTime - context.Output.StartTime).TotalSeconds;
        var offset = elapsed * speed % period;

        var x1 = -offset;
        LayoutContainer.SetMarginLeft(titleLabels[0], x1);
        LayoutContainer.SetMarginRight(titleLabels[0], x1 + contentWidth);

        var x2 = period - offset;
        LayoutContainer.SetMarginLeft(titleLabels[1], x2);
        LayoutContainer.SetMarginRight(titleLabels[1], x2 + contentWidth);
    }
}
