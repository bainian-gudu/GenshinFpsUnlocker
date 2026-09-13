namespace GenshinFpsUnlocker.Host;

/// <summary>托盘菜单自绘：圆角选中条、设计稿紫强调色与配套配色表。</summary>
internal sealed partial class MainForm
{
    /// <summary>
    /// 自绘渲染器。画笔/画刷在构造时建好、 Dispose 时释放：菜单每次鼠标移动都会
    /// 重绘若干项，早先每次 OnRender* 都 new SolidBrush/Pen，一次悬停就产生几十个
    /// GDI+ 对象和等量的 GC 压力。
    /// </summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer, IDisposable
    {
        private readonly bool _dark;
        private readonly SolidBrush _bgBrush;
        private readonly SolidBrush _hoverBrush;
        private readonly SolidBrush _accentBrush;
        private readonly Pen _checkPen;
        private readonly Pen _sepPen;
        private bool _disposed;
        private readonly Color _bg;
        private readonly Color _hover;
        private readonly Color _accent;
        private readonly Color _text;
        private readonly Color _muted;
        private readonly Color _sep;

        public TrayMenuRenderer(bool dark)
            : base(new TrayColorTable(dark))
        {
            _dark = dark;
            _bg = dark ? Color.FromArgb(0x1B, 0x1D, 0x25) : Color.FromArgb(0xF7, 0xF6, 0xFA);
            _hover = dark ? Color.FromArgb(0x25, 0x26, 0x31) : Color.FromArgb(0xEE, 0xEA, 0xF6);
            _accent = dark ? Color.FromArgb(0xBD, 0xA2, 0xF2) : Color.FromArgb(0x90, 0x6A, 0xC7);
            _text = dark ? Color.FromArgb(0xED, 0xEC, 0xF3) : Color.FromArgb(0x1A, 0x1A, 0x22);
            _muted = dark ? Color.FromArgb(0x8B, 0x8C, 0x9C) : Color.FromArgb(0x77, 0x70, 0x82);
            _sep = dark ? Color.FromArgb(0x2D, 0x2E, 0x3A) : Color.FromArgb(0xE0, 0xDC, 0xEA);
            _bgBrush = new SolidBrush(_bg);
            _hoverBrush = new SolidBrush(_hover);
            _accentBrush = new SolidBrush(_accent);
            _sepPen = new Pen(_sep);
            _checkPen = new Pen(_accent, 1.9f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            RoundedEdges = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bgBrush.Dispose();
            _hoverBrush.Dispose();
            _accentBrush.Dispose();
            _sepPen.Dispose();
            _checkPen.Dispose();
            GC.SuppressFinalize(this);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.FillRectangle(_bgBrush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = e.AffectedBounds;
            r.Width -= 1;
            r.Height -= 1;
            // Win10 兜底路径下窗口四角被 Region 裁掉，边框也要画成同样的圆角矩形，
            // 否则边框会在被裁掉的角上断开（Win11 走 DWM 圆角，边框保持直角矩形即可：
            // DWM 的圆角比这 1px 边框更靠外，裁不到它）。
            if (TrayMenuCorners.RegionActive && e.ToolStrip is not null)
            {
                var g = e.Graphics;
                var prev = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = TrayMenuCorners.RoundRect(r, TrayMenuCorners.RadiusFor(e.ToolStrip));
                g.DrawPath(_sepPen, path);
                g.SmoothingMode = prev;
                return;
            }

            e.Graphics.DrawRectangle(_sepPen, r);
        }

        /// <summary>勾选槽宽：√ 固定画在这一槽内（水平居中于槽、垂直居中于行）。</summary>
        private const int CheckGutter = 28;

        /// <summary>文字和分隔线的左缘，保证菜单项统一左对齐。</summary>
        private const int TextLeft = 32;

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 无系统图标栏
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // 不使用系统默认勾选绘制；在 OnRenderItemText 前由 DrawCheckMark 绘制
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var item = e.Item;
            var bounds = new Rectangle(3, 1, Math.Max(0, item.Width - 6), Math.Max(0, item.Height - 2));

            g.FillRectangle(_bgBrush, new Rectangle(0, 0, item.Width, item.Height));

            if (!item.Enabled) return;
            if (!item.Selected && !item.Pressed) return;

            using var path = TrayMenuCorners.RoundRect(bounds, 6);
            g.FillPath(_hoverBrush, path);
            g.FillRectangle(_accentBrush, new Rectangle(bounds.X + 1, bounds.Y + 5, 3, Math.Max(4, bounds.Height - 10)));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var item = e.Item;
            var isCheck = item is ToolStripMenuItem { CheckOnClick: true };
            var isChecked = item is ToolStripMenuItem { Checked: true };

            // 勾选标记：固定画在 gutter 内垂直居中
            if (isChecked)
                DrawCheckMark(g, item);

            e.TextColor = !item.Enabled
                ? _muted
                : isChecked && isCheck
                    ? _accent
                    : item.Selected
                        ? _accent
                        : _text;

            // 文字从勾选槽右侧开始左对齐，并在整行高度内垂直居中。
            // 右侧预留箭头和边距，避免「修改帧率」子菜单项的文字盖住箭头。
            var font = e.TextFont ?? item.Font ?? SystemFonts.MenuFont ?? SystemFonts.DefaultFont;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            var textRect = new Rectangle(
                TextLeft,
                0,
                Math.Max(8, item.Width - TextLeft - 12),
                item.Height);

            TextRenderer.DrawText(
                g,
                e.Text ?? item.Text ?? "",
                font,
                textRect,
                e.TextColor,
                flags
                    | TextFormatFlags.Left
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine);
            // 不再调用 base，避免系统再画一次偏移文字
        }

        /// <summary>
        /// 自绘勾选标记：与文字共用「整行高度的中线」这一条基线，勾形包围盒
        /// （11×8）在勾选槽内水平居中、在行内垂直居中，√ 与文字因此严格对齐。
        /// 早先的写法把 ContentRectangle.Y 又加了一遍行高的一半，√ 比文字低 3~4px。
        /// </summary>
        private void DrawCheckMark(Graphics g, ToolStripItem item)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const float glyphW = 11f;   // 勾形包围盒宽
            const float glyphH = 8f;    // 勾形包围盒高（cy-4 .. cy+4）
            var cx = (CheckGutter - glyphW) / 2f;
            var cy = item.Height / 2f;
            g.DrawLines(_checkPen, new[]
            {
                new PointF(cx, cy),
                new PointF(cx + 4, cy + glyphH / 2f),
                new PointF(cx + glyphW, cy - glyphH / 2f),
            });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
            e.Graphics.DrawLine(_sepPen, TextLeft, y, Math.Max(TextLeft + 8, e.Item.Width - 12), y);
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        private readonly Color _bg;
        private readonly Color _hover;

        public TrayColorTable(bool dark)
        {
            _bg = dark ? Color.FromArgb(0x1B, 0x1D, 0x25) : Color.FromArgb(0xF7, 0xF6, 0xFA);
            _hover = dark ? Color.FromArgb(0x25, 0x26, 0x31) : Color.FromArgb(0xEE, 0xEA, 0xF6);
        }

        public override Color MenuBorder => _bg;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuStripGradientBegin => _bg;
        public override Color MenuStripGradientEnd => _bg;
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color SeparatorDark => _bg;
        public override Color SeparatorLight => _bg;
        public override Color CheckBackground => _bg;
        public override Color CheckSelectedBackground => _hover;
        public override Color CheckPressedBackground => _hover;
    }
}
