using UnityEngine;

namespace Skopje.BossHealth
{
    /// <summary>
    /// Look of the boss health bar. This is the file to edit.
    ///
        /// Sprites are chosen by the name shown on an Image's Sprite field in the game.
        /// Leave a fill name empty to use a solid rectangle.
        /// After changing a name, rebuild the plugin.
    /// </summary>
    public static class BossBarStyle
    {
        public const string HealthSpriteName = "";
        public const string DelaySpriteName = "";
        public const string BackgroundSpriteName = "";

        public static readonly Color HealthColor = new Color(0.86f, 0.1f, 0.1f, 1f);
        public static readonly Color DelayColor = new Color(1f, 0.48f, 0.08f, 1f);
        public static readonly Color ImmuneColor = new Color(0.62f, 0.62f, 0.62f, 1f);
        public static readonly Color ImmuneDelayColor = new Color(0.4f, 0.4f, 0.4f, 1f);
        public static readonly Color BackgroundColor = new Color(0.04f, 0.04f, 0.04f, 0.8f);
        public static readonly Color LabelColor = Color.white;

        // The orange bar waits this long after the last hit, then drains toward the real health.
        public const float DelaySeconds = 0.6f;
        public const float DelayDrainPerSecond = 0.75f;

        public static readonly Vector2 BarSize = new Vector2(392f, 13f);
        public const float TopClearance = 25f;
        public const float TrackPadding = 2f;
        public const float LabelHeight = 26f;
        public const float LabelFontSize = 20f;
    }
}
