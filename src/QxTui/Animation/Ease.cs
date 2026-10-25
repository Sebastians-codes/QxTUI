namespace QxTui.Animation;

public enum Easing
{
    Linear,
    InQuad,
    OutQuad,
    InOutQuad,
    OutCubic,
    InOutCubic,
    OutBack,
}

/// <summary>Pure easing functions. t in [0,1] → eased value (may overshoot for OutBack).</summary>
public static class Ease
{
    public static float Apply(Easing easing, float t) => easing switch
    {
        Easing.Linear => t,
        Easing.InQuad => t * t,
        Easing.OutQuad => t * (2f - t),
        Easing.InOutQuad => t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t,
        Easing.OutCubic => 1f + (t - 1f) * (t - 1f) * (t - 1f),
        Easing.InOutCubic => t < 0.5f ? 4f * t * t * t : 1f - (2f - 2f * t) * (2f - 2f * t) * (2f - 2f * t) / 2f,
        Easing.OutBack => 1f + 2.70158f * (t - 1f) * (t - 1f) * (t - 1f) + 1.70158f * (t - 1f) * (t - 1f),
        _ => t,
    };
}
