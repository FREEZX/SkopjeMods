using UnityEngine;

namespace Skopje.BossHealth
{
    /// <summary>
    /// Look of the boss health bar. This is the file to edit.
    ///
    /// Sprites are chosen by the name shown on an Image's Sprite field in the game.
    /// Leave a name empty to copy the in-game XP progress bar sprite.
    /// After changing a name, rebuild the plugin.
    /// </summary>
    public static class BossBarStyle
    {
        public const string HealthSpriteName = "";
        public const string DelaySpriteName = "";
        public const string BackgroundSpriteName = "";

        public static readonly Color HealthColor = new Color(0.86f, 0.1f, 0.1f, 1f);
        public static readonly Color DelayColor = new Color(1f, 0.48f, 0.08f, 1f);
        public static readonly Color BackgroundColor = new Color(0.04f, 0.04f, 0.04f, 0.8f);
        public static readonly Color LabelColor = Color.white;

        // The white bar waits this long after the last hit, then drains toward the real health.
        public const float DelaySeconds = 0.6f;
        public const float DelayDrainPerSecond = 0.75f;

        public static readonly Vector2 BarSize = new Vector2(392f, 13f);
        public const float TopClearance = 100f;
        public const float TrackPadding = 2f;
        public const float LabelHeight = 26f;
        public const float LabelFontSize = 20f;
    }
}
