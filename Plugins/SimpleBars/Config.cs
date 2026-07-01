namespace SimpleBars
{
    using System;
    using System.Numerics;
    using GameHelper.Localization;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Saves config of each type of healthbar.
    /// </summary>
    public class Config
    {
        private const int MinBarScaleX = 48;
        private const int MaxBarScaleX = 500;
        private const int MinBarScaleY = 2;
        private const int MaxBarScaleY = 128;

        /// <summary>
        ///     Enables the healthbar
        /// </summary>
        public bool Enable;

        /// <summary>
        ///     Change texture if player can cull strike this healthbar.
        /// </summary>
        public bool ShowCullStrike;

        /// <summary>
        ///     Show the absolute Health + Es as text aboved the healthbar
        /// </summary>
        public bool ShowText;

        /// <summary>
        ///     Gets the color to apply on healthbar background.
        /// </summary>
        public Vector4 BackgroundColor;

        /// <summary>
        ///     Gets the color to apply on healthbar.
        /// </summary>
        public Vector4 HealthbarColor;

        /// <summary>
        ///     Gets the color to apply on ES bar.
        /// </summary>
        public Vector4 ESColor;

        /// <summary>
        ///     Gets the color to apply on Mana bar.
        /// </summary>
        public Vector4 ManaColor;

        /// <summary>
        ///     Show Mana bar below the health bar.
        /// </summary>
        public bool ShowManaBar;

        /// <summary>
        ///     Vertical gap (in pixels) between stacked bars (ES/Mana and Health).
        /// </summary>
        public float BarGap;

        /// <summary>
        ///     Use gradient textures (full_bar.png) for bars. If false, use flat colors.
        /// </summary>
        public bool UseGradientBars;

        /// <summary>
        ///     Multiplier for black border thickness around bars.
        /// </summary>
        public float BorderThicknessScale;

        /// <summary>
        ///     Show the main Health bar.
        /// </summary>
        public bool ShowHealthBar;

        /// <summary>
        ///     Show the ES bar.
        /// </summary>
        public bool ShowESBar;

        /// <summary>
        ///     Draw a background rectangle behind the text.
        /// </summary>
        public bool ShowTextBackground;

        /// <summary>
        ///     Background color for the text, when enabled.
        /// </summary>
        public Vector4 TextBackgroundColor;

        /// <summary>
        ///     Render bars as a circular dot + arcs instead of rectangles.
        /// </summary>
        public bool UseCircleDot;

        /// <summary>
        ///     Circle radius for dot mode. If 0, derived from scale.
        /// </summary>
        public float CircleRadius;

        /// <summary>
        ///     Arc thickness for ES/Mana half-circles in dot mode.
        /// </summary>
        public float CircleArcThickness;
        /// <summary>
        ///     Scale multiplier for the circle dot size.
        /// </summary>
        public float CircleScale;

        /// <summary>
        ///     Background dot radius for circle mode. Set 0 to hide.
        /// </summary>
        public float CircleBackgroundRadius;

        /// <summary>
        ///     Background dot color for circle mode.
        /// </summary>
        public Vector4 CircleBackgroundColor;

        /// <summary>
        ///     Radial offset (pixels) for ES/Mana half-circles away from the health circle.
        /// </summary>
        public float CircleArcOffset;

        /// <summary>
        ///     Use individual scales for health, ES, and mana bars (self only).
        /// </summary>
        public bool UseIndividualBarScale;

        /// <summary>
        ///     Individual scale for Health bar (self only).
        /// </summary>
        public Vector2 HealthScale;

        /// <summary>
        ///     Individual scale for ES bar (self only).
        /// </summary>
        public Vector2 ESScale;

        /// <summary>
        ///     Individual scale for Mana bar (self only).
        /// </summary>
        public Vector2 ManaScale;


        /// <summary>
        ///     Gets the color of the next.
        /// </summary>
        public Vector4 TextColor;

        /// <summary>
        ///     Healthbar size multiplier
        /// </summary>
        public Vector2 Scale;

        /// <summary>
        ///     Healthbar position shift.
        /// </summary>
        public Vector2 Shift;

        /// <summary>
        ///     Gets the half of the scale value.
        /// </summary>
        public Vector2 HalfOfScale;

        /// <summary>
        ///     Total number of Graduations on this healthbar
        /// </summary>
        public int Graduations;

        /// <summary>
        ///     Stores the start location of any given Graduation.
        /// </summary>
        public float GraduationsLocationStart;

        /// <summary>
        ///     Stores the end location of any given Graduation.
        /// </summary>
        public Vector2 GraduationsLocationEnd;

        /// <summary>
        ///     Show HP Gradation Marks on the health bar.
        /// </summary>
        public bool ShowHPGraduations;

        /// <summary>
        ///     Show ES Gradation Marks on the ES bar (self only).
        /// </summary>
        public bool ShowESGraduations;

        /// <summary>
        ///     Total number of Graduations on the ES bar (self only).
        /// </summary>
        public int ESGraduations;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="graduations">number of graduations on the healthbar</param>
        /// <param name="showText">show the absolute Health + Es as text aboved the healthbar</param>
        /// <param name="sizeY">healthbar default size Y axis.</param>
        public Config(Vector4 healthbarcolor, int graduations, bool showText, float sizeY)
        {
            this.Enable = true;
            this.ShowCullStrike = true;
            this.ShowText = showText;
            this.BackgroundColor = new(Vector3.Zero, 1f);
            this.HealthbarColor = healthbarcolor;
            this.ESColor = new(0f, 1f, 1f, 1f);
            this.TextColor = new(0f, 1f, 1f, 1f);
            this.ManaColor = new(0f, 0.5f, 1f, 1f); // blue
            this.ShowManaBar = false;
            this.BarGap = 2f;
            this.UseGradientBars = false;
            this.BorderThicknessScale = 1f;
            this.ShowHealthBar = true;
            this.ShowESBar = true;
            this.ShowTextBackground = false;
            this.TextBackgroundColor = new(0f, 0f, 0f, 0.6f);
            this.UseCircleDot = false;
            this.CircleRadius = 8f;
            this.CircleArcThickness = 2f;
            this.CircleScale = 1f;
            this.CircleBackgroundRadius = 10f;
            this.CircleBackgroundColor = new Vector4(0f, 0f, 0f, 1f);
            this.CircleArcOffset = 0f;
            this.Scale = new(128f, sizeY);
            this.HalfOfScale = this.Scale / 2;
            this.UseIndividualBarScale = false;
            this.HealthScale = this.Scale;
            this.ESScale = this.Scale;
            this.ManaScale = this.Scale;
            this.Shift = new(0f, 11f);
            this.Graduations = graduations;
            this.GraduationsLocationStart = 0f;
            this.GraduationsLocationEnd = Vector2.Zero;
            this.ShowHPGraduations = true;
            this.ShowESGraduations = false;
            this.ESGraduations = 0;
            this.UpdateGrauationsLocationData();
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="sizeY">healthbar default size Y axis.</param>
        public Config(Vector4 healthbarcolor, float sizeY) :
            this(healthbarcolor, 0, false, sizeY) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        /// <param name="graduations">number of graduations on the healthbar</param>
        public Config(Vector4 healthbarcolor, int graduations) :
            this(healthbarcolor, graduations, false, 8f) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        /// <param name="healthbarcolor">color of the healthbar</param>
        public Config(Vector4 healthbarcolor) :
            this(healthbarcolor, 0) { }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Config" /> class.
        /// </summary>
        [JsonConstructor]
        public Config() :
            this(Vector4.One) { }

        /// <summary>
        ///     Display the Config on imgui.
        /// </summary>
        public void Draw(PluginLocalization text, bool isSelf = false, TextureLoader? textures = null, bool useGradientGlobal = false)
        {
            ImGui.Text(text.T("config.manual_edit_note", "NOTE: For going above/below the limit, or for manual editing, press CTRL + Left Mouse Button click."));
            if (ImGui.BeginTable("config_table", 2))
            {
                // Left column toggles (Self ordering)
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.enable_bars", "Enable bars", "SimpleBarsConfigEnableBars"), ref this.Enable);
                ImGui.TableNextColumn();
                // Right column aligned control next to Enable: Shift
                ImGuiHelper.Vector2SliderInt(text.Label("config.shift", "Shift (x, y)", "SimpleBarsConfigShift"), ImGui.GetColumnWidth(), ref this.Shift, -4000, 4000, -2500, 2500, ImGuiSliderFlags.Logarithmic);

                // Use gradient textures (placed directly under Enable bars)
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.use_gradient_textures", "Use gradient textures", "SimpleBarsConfigUseGradient"), ref this.UseGradientBars);
                ImGui.TableNextColumn();
                // No right-side control

                // Use circle dot mode
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.use_circle_dot", "Use circle dot", "SimpleBarsConfigUseCircleDot"), ref this.UseCircleDot);
                ImGui.TableNextColumn();
                ImGui.DragFloat(text.Label("config.circle_scale", "Circle scale", "SimpleBarsConfigCircleScale"), ref this.CircleScale, 0.01f, 0.5f, 3.0f, "%.2fx");
                ImGui.TableNextColumn();
                ImGui.Text("");
                ImGui.TableNextColumn();
                ImGui.DragFloat(text.Label("config.background_radius", "Background radius", "SimpleBarsConfigBackgroundRadius"), ref this.CircleBackgroundRadius, 0.5f, 0f, 128f, "%.1f px");
                ImGui.TableNextColumn();
                ImGui.Text("");
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("config.background_dot", "Background dot", "SimpleBarsConfigBackgroundDot"), ref this.CircleBackgroundColor);
                ImGui.TableNextColumn();
                ImGui.Text("");
                ImGui.TableNextColumn();
                ImGui.DragFloat(text.Label("config.arc_offset", "Arc offset (px)", "SimpleBarsConfigArcOffset"), ref this.CircleArcOffset, 0.5f, 0f, 128f, "%.1f px");

                // Spacer row before sections below
                ImGui.TableNextColumn(); ImGui.Text("");
                ImGui.TableNextColumn(); ImGui.Text("");

                // Show ES Bar
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.show_energy_shield", "Show Energy Shield", "SimpleBarsConfigShowES"), ref this.ShowESBar);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("config.es_bar_color", "ES Bar", "SimpleBarsConfigEsBarColor"), ref this.ESColor);
                if (isSelf && this.UseIndividualBarScale)
                {
                    ImGuiHelper.Vector2SliderInt(text.Label("config.es_scale", "ES Scale (x, y)", "SimpleBarsConfigEsScale"), ImGui.GetColumnWidth(), ref this.ESScale, MinBarScaleX, MaxBarScaleX, MinBarScaleY, MaxBarScaleY, ImGuiSliderFlags.Logarithmic);
                }

                // Show Health Bar
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.show_health", "Show Health", "SimpleBarsConfigShowHealth"), ref this.ShowHealthBar);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("config.healthbar_color", "Healthbar", "SimpleBarsConfigHealthbarColor"), ref this.HealthbarColor);
                // Background color directly under Healthbar color
                ImGui.TableNextColumn(); ImGui.Text("");
                ImGui.TableNextColumn(); ImGui.ColorEdit4(text.Label("config.background_color", "Background", "SimpleBarsConfigBackgroundColor"), ref this.BackgroundColor);
                if (isSelf && this.UseIndividualBarScale)
                {
                    if (ImGuiHelper.Vector2SliderInt(text.Label("config.health_scale", "Health Scale (x, y)", "SimpleBarsConfigHealthScale"), ImGui.GetColumnWidth(), ref this.HealthScale, MinBarScaleX, MaxBarScaleX, MinBarScaleY, MaxBarScaleY, ImGuiSliderFlags.Logarithmic))
                    {
                        this.UpdateGrauationsLocationData();
                    }
                }
                else
                {
                    if (ImGuiHelper.Vector2SliderInt(text.Label("config.scale", "Scale (x, y)", "SimpleBarsConfigScale"), ImGui.GetColumnWidth(), ref this.Scale, MinBarScaleX, MaxBarScaleX, MinBarScaleY, MaxBarScaleY, ImGuiSliderFlags.Logarithmic))
                    {
                        this.UpdateGrauationsLocationData();
                    }
                }

                ImGuiHelper.ToolTip(text.T("config.scale.tooltip", "By default texture is of height 16, " +
                    "If increasing the Y axis ruins the texture, " +
                    "feel free to modify the texture height via your fav texture editor. " +
                    "This doesn't apply to x axis."));
                // Show Mana Bar
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.show_mana", "Show Mana", "SimpleBarsConfigShowMana"), ref this.ShowManaBar);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("config.mana_bar_color", "Mana Bar", "SimpleBarsConfigManaBarColor"), ref this.ManaColor);
                if (isSelf && this.UseIndividualBarScale)
                {
                    ImGuiHelper.Vector2SliderInt(text.Label("config.mana_scale", "Mana Scale (x, y)", "SimpleBarsConfigManaScale"), ImGui.GetColumnWidth(), ref this.ManaScale, MinBarScaleX, MaxBarScaleX, MinBarScaleY, MaxBarScaleY, ImGuiSliderFlags.Logarithmic);
                }

                // Visualize culling strike
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.visualize_culling_strike", "Visualize Culling Strike Range", "SimpleBarsConfigVisualizeCull"), ref this.ShowCullStrike);
                ImGui.TableNextColumn();
                // No right-side control

                // Show text
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.show_health_es_text", "Show health+ES (absolute) as text", "SimpleBarsConfigShowText"), ref this.ShowText);
                ImGui.TableNextColumn();
                ImGui.ColorEdit4(text.Label("config.text_color", "Text Color", "SimpleBarsConfigTextColor"), ref this.TextColor);
                ImGui.TableNextColumn(); ImGui.Text("");
                ImGui.TableNextColumn(); ImGui.Checkbox(text.Label("config.show_text_background", "Show text background", "SimpleBarsConfigShowTextBackground"), ref this.ShowTextBackground);
                ImGui.TableNextColumn(); ImGui.Text("");
                ImGui.TableNextColumn(); ImGui.ColorEdit4(text.Label("config.text_background_color", "Text background", "SimpleBarsConfigTextBackgroundColor"), ref this.TextBackgroundColor);

                // HP Gradation Marks
                ImGui.TableNextColumn();
                ImGui.Checkbox(text.Label("config.hp_gradation_marks", "HP Gradation Marks", "SimpleBarsConfigHpGradationMarks"), ref this.ShowHPGraduations);
                ImGuiHelper.ToolTip(text.T("config.gradation_marks.tooltip", "Graduation thickness depends on Font size. Also, Gradation marks are expensive to draw."));
                ImGui.TableNextColumn();
                if (this.ShowHPGraduations)
                {
                    if (ImGui.DragInt(text.Label("config.hp_marks", "HP Marks", "hpgrad"), ref this.Graduations, 0.05f, 0, 9))
                    {
                        this.UpdateGrauationsLocationData();
                    }
                }
                else
                {
                    ImGui.Text("");
                }

                // ES Gradation Marks (self only)
                if (isSelf)
                {
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(text.Label("config.es_gradation_marks", "ES Gradation Marks", "SimpleBarsConfigEsGradationMarks"), ref this.ShowESGraduations);
                    ImGui.TableNextColumn();
                    if (this.ShowESGraduations)
                    {
                        ImGui.DragInt(text.Label("config.es_marks", "ES Marks", "esgrad"), ref this.ESGraduations, 0.05f, 0, 9);
                    }
                    else
                    {
                        ImGui.Text("");
                    }
                }

                // Left column: Bar Gap
                ImGui.TableNextColumn();
                ImGui.DragFloat(text.Label("config.bar_gap", "Bar Gap (px)", "SimpleBarsConfigBarGap"), ref this.BarGap, 0.5f, 0f, 12f);
                ImGui.TableNextColumn();
                ImGui.Text("");

                // Left column: Border thickness
                ImGui.TableNextColumn();
                ImGui.DragFloat(text.Label("config.border_thickness", "Border thickness", "SimpleBarsConfigBorderThickness"), ref this.BorderThicknessScale, 0.05f, 0.25f, 4f);
                ImGui.TableNextColumn();
                ImGui.Text("");

                // Self-only controls for individual scales
                if (isSelf)
                {
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(text.Label("config.use_individual_bar_scales", "Use individual bar scales (self)", "SimpleBarsConfigUseIndividualScales"), ref this.UseIndividualBarScale);
                    ImGui.TableNextColumn();
                    ImGui.Text(this.UseIndividualBarScale
                        ? text.T("config.using_individual_scales", "Using individual scales above")
                        : text.T("config.using_main_scale", "Uses main Scale/Health Scale"));
                }
                ImGui.EndTable();
            }

            // --- Live Preview ---
            ImGui.Separator();
            ImGui.Text(text.T("config.preview", "Preview"));
            float pad = 1f;
            float borderThickness = MathF.Max(1f, ImGui.GetFontSize() / 12f) * this.BorderThicknessScale;
            float gradThickness = ImGui.GetFontSize() / 9f;

            // Determine scales to preview
            var baseScale = (this.UseIndividualBarScale && isSelf) ? this.HealthScale : this.Scale;
            var esScale = (this.UseIndividualBarScale && isSelf) ? this.ESScale : this.Scale;
            var manaScale = (this.UseIndividualBarScale && isSelf) ? this.ManaScale : this.Scale;
            bool showHealth = this.ShowHealthBar;
            bool showES = this.ShowESBar;
            bool showMana = this.ShowManaBar;

            // Compute preview area size
            if (this.UseCircleDot)
            {
                // Compact preview height/width for circle
                float rBase = this.CircleRadius > 0 ? this.CircleRadius : MathF.Max(6f, baseScale.Y);
                float r = rBase * this.CircleScale;
                float arcThick = MathF.Max(1.0f, this.CircleArcThickness * this.CircleScale);
                float bgR = MathF.Max(0f, this.CircleBackgroundRadius);
                float maxR = MathF.Max(r, bgR);
                float dotPreviewHeight = (maxR * 2f) + (arcThick * 2f) + 20f;
                ImGui.BeginChild($"preview_{GetHashCode()}", new Vector2(MathF.Max(220f, ImGui.GetContentRegionAvail().X), dotPreviewHeight), ImGuiChildFlags.Borders);
                var dl2 = ImGui.GetWindowDrawList();
                var p0b = ImGui.GetCursorScreenPos();
                var center = p0b + new Vector2(40f, dotPreviewHeight / 2f);

                float hPctDot = 65f;
                float esPctDot = 40f;
                float mPctDot = 75f;

                // Background dot behind everything
                if (bgR > 0f)
                {
                    dl2.AddCircleFilled(center, bgR, ImGuiHelper.Color(this.CircleBackgroundColor));
                }
                // Main circle background
                dl2.AddCircleFilled(center, r, ImGuiHelper.Color(this.BackgroundColor));
                // Subtle border around main circle
                dl2.AddCircle(center, r, 0xFF000000, 64, borderThickness);

                // Health pie wedge
                if (showHealth)
                {
                    float angle = (MathF.PI * 2f) * (hPctDot / 100f);
                    float start = -MathF.PI / 2f;
                    dl2.PathClear();
                    dl2.PathArcTo(center, r, start, start + angle, 48);
                    dl2.PathLineTo(center);
                    dl2.PathFillConvex(ImGuiHelper.Color(this.HealthbarColor));
                }

                // ES top half arc
                if (showES)
                {
                    float start = -MathF.PI; // left
                    float angle = MathF.PI * (esPctDot / 100f);
                    float rArc = r + MathF.Max(0f, this.CircleArcOffset) + (arcThick / 2f);
                    // Colored arc only
                    dl2.PathClear();
                    dl2.PathArcTo(center, rArc, start, start + angle, 32);
                    dl2.PathStroke(ImGuiHelper.Color(this.ESColor), ImDrawFlags.None, arcThick);
                }

                // Mana bottom half arc
                if (showMana)
                {
                    float start = 0f; // right
                    float angle = MathF.PI * (mPctDot / 100f);
                    float rArc = r + MathF.Max(0f, this.CircleArcOffset) + (arcThick / 2f);
                    // Colored arc only
                    dl2.PathClear();
                    dl2.PathArcTo(center, rArc, start, start + angle, 32);
                    dl2.PathStroke(ImGuiHelper.Color(this.ManaColor), ImDrawFlags.None, arcThick);
                }

                // Text above ES top
                if (this.ShowText)
                {
                    string tText = "123.4K";
                    var size = ImGui.CalcTextSize(tText);
                    var tPad = new Vector2(2f, 2f);
                    float safeGap = 1f;
                    var bgTopLeft = new Vector2(center.X - r, center.Y - r - (size.Y + (tPad.Y * 2f) + safeGap));
                    var bgBottomRight = new Vector2(center.X - r + (size.X + (tPad.X * 2f)), center.Y - r - safeGap);
                    var drawPos = bgTopLeft + tPad;
                    if (this.ShowTextBackground)
                    {
                        dl2.AddRectFilled(bgTopLeft, bgBottomRight, ImGuiHelper.Color(this.TextBackgroundColor));
                    }
                    dl2.AddText(drawPos, ImGuiHelper.Color(this.TextColor), tText);
                }

                ImGui.EndChild();
                return;
            }

            float above = showHealth && showES ? (esScale.Y + this.BarGap) : 0f;
            float below = showHealth && showMana ? (manaScale.Y + this.BarGap) : 0f;
            float core = showHealth ? baseScale.Y : 0f;
            float extra = (!showHealth ? ((showES ? esScale.Y : 0f) + (showMana ? manaScale.Y : 0f)) : 0f);
            float previewHeight = above + core + below + extra + 8f;
            float previewWidth = MathF.Max(baseScale.X, MathF.Max(showES ? esScale.X : 0f, showMana ? manaScale.X : 0f)) + 16f;

            ImGui.BeginChild($"preview_{GetHashCode()}", new Vector2(MathF.Max(220f, ImGui.GetContentRegionAvail().X), previewHeight + 16f), ImGuiChildFlags.Borders);
            var dl = ImGui.GetWindowDrawList();
            var p0 = ImGui.GetCursorScreenPos();
            var origin = p0 + new Vector2(8f, 8f + above + (!showHealth ? (-extra / 2f) : 0f));

            // Sample percents
            float hPct = 65f;
            float esPct = 40f;
            float mPct = 75f;

            // Background and Health
            var hStart = origin;
            var hEnd = hStart + baseScale;
            bool useGradient = useGradientGlobal || this.UseGradientBars;
            if (showHealth)
            {
                dl.AddRectFilled(hStart, hEnd, ImGuiHelper.Color(this.BackgroundColor));
                if (useGradient && textures != null)
                {
                    var (hb, _, _) = textures.GetTexture("full_bar.png");
                    dl.AddImage(hb, hStart, hStart + new Vector2(baseScale.X * (hPct / 100f), baseScale.Y), Vector2.Zero, Vector2.One, ImGuiHelper.Color(this.HealthbarColor));
                }
                else
                {
                    dl.AddRectFilled(hStart, hStart + new Vector2(baseScale.X * (hPct / 100f), baseScale.Y), ImGuiHelper.Color(this.HealthbarColor));
                }
                // Border around health
                dl.AddRect(hStart - new Vector2(pad, pad), hEnd + new Vector2(pad, pad), 0xFF000000, 0f, ImDrawFlags.None, borderThickness);
            }

            // HP Graduations
            var tmp = hStart - Vector2.UnitY;
            float gradStep = (this.UseIndividualBarScale && isSelf) ? (baseScale.X / (this.Graduations + 1f)) : this.GraduationsLocationStart;
            Vector2 gradEnd = (this.UseIndividualBarScale && isSelf) ? (Vector2.UnitY * baseScale.Y) : this.GraduationsLocationEnd;
            if (this.ShowHPGraduations)
            {
                for (int i = 0; i < this.Graduations; i++)
                {
                    tmp.X += gradStep;
                    dl.AddLine(tmp, tmp + gradEnd, 0xFF000000, gradThickness);
                }
            }

            // ES (above)
            Vector2 esPreviewStart = origin - new Vector2(0f, showHealth ? (esScale.Y + this.BarGap) : (showMana ? esScale.Y : (esScale.Y / 2f)));
            if (showES)
            {
                var esEnd2 = esPreviewStart + esScale;
                if (useGradient && textures != null)
                {
                    var (esTex, _, _) = textures.GetTexture("full_bar.png");
                    dl.AddImage(esTex, esPreviewStart, esPreviewStart + new Vector2(esScale.X * (esPct / 100f), esScale.Y), Vector2.Zero, Vector2.One, ImGuiHelper.Color(this.ESColor));
                }
                else
                {
                    dl.AddRectFilled(esPreviewStart, esPreviewStart + new Vector2(esScale.X * (esPct / 100f), esScale.Y), ImGuiHelper.Color(this.ESColor));
                }
                dl.AddRect(esPreviewStart - new Vector2(pad, pad), esEnd2 + new Vector2(pad, pad), 0xFF000000, 0f, ImDrawFlags.None, borderThickness);
            }

            // ES Graduations (self only preview)
            if (isSelf && showES && this.ShowESGraduations && this.ESGraduations > 0)
            {
                float esGradStep = esScale.X / (this.ESGraduations + 1f);
                Vector2 esGradEnd = Vector2.UnitY * esScale.Y;
                var esGradTmp = esPreviewStart - Vector2.UnitY;
                for (int i = 0; i < this.ESGraduations; i++)
                {
                    esGradTmp.X += esGradStep;
                    dl.AddLine(esGradTmp, esGradTmp + esGradEnd, 0xFF000000, gradThickness);
                }
            }

            // Mana (below)
            if (showMana)
            {
                var mTopLeft = showHealth ? (origin + new Vector2(0f, baseScale.Y + this.BarGap)) : (origin + new Vector2(0f, showES ? 0f : -(manaScale.Y / 2f)));
                var mStart = mTopLeft;
                var mEnd2 = mStart + manaScale;
                if (useGradient && textures != null)
                {
                    var (manaTex, _, _) = textures.GetTexture("full_bar.png");
                    dl.AddImage(manaTex, mStart, mStart + new Vector2(manaScale.X * (mPct / 100f), manaScale.Y), Vector2.Zero, Vector2.One, ImGuiHelper.Color(this.ManaColor));
                }
                else
                {
                    dl.AddRectFilled(mStart, mStart + new Vector2(manaScale.X * (mPct / 100f), manaScale.Y), ImGuiHelper.Color(this.ManaColor));
                }
                dl.AddRect(mStart - new Vector2(pad, pad), mEnd2 + new Vector2(pad, pad), 0xFF000000, 0f, ImDrawFlags.None, borderThickness);
            }

            // Text above if enabled (anchored above ES if shown, else above Health, else Mana)
            if (this.ShowText)
            {
                Vector2 tAnchor = showES ? (showHealth ? (origin - new Vector2(0f, esScale.Y + this.BarGap)) : (origin - new Vector2(0f, showMana ? esScale.Y : (esScale.Y / 2f))))
                                         : (showHealth ? hStart : (showMana ? (origin + new Vector2(0f, (showHealth ? baseScale.Y + this.BarGap : 0f))) : origin));
                string tText = "123.4K";
                var tSize = ImGui.CalcTextSize(tText);
                var tPad = new Vector2(2f, 2f);
                float safeGap = 1f;
                // Background spans the text width; align left to the bar's left (anchor X)
                var bgTopLeft = new Vector2(tAnchor.X, tAnchor.Y - (tSize.Y + (tPad.Y * 2f) + safeGap));
                var bgBottomRight = new Vector2(tAnchor.X + (tSize.X + (tPad.X * 2f)), tAnchor.Y - safeGap);
                var textPos = bgTopLeft + tPad;
                if (this.ShowTextBackground)
                {
                    dl.AddRectFilled(bgTopLeft, bgBottomRight, ImGuiHelper.Color(this.TextBackgroundColor));
                }
                dl.AddText(textPos, ImGuiHelper.Color(this.TextColor), tText);
            }

            ImGui.EndChild();
        }

        private void UpdateGrauationsLocationData()
        {
            this.GraduationsLocationStart = this.Scale.X / (this.Graduations + 1);
            this.GraduationsLocationEnd = Vector2.UnitY * this.Scale.Y;
            this.HalfOfScale = this.Scale / 2;
        }
    }
}

