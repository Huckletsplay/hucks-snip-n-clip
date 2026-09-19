using System;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class CaptureActionMenuRow : UserControl
    {
        private const int ActionWidth = 144;
        private const int MinimumBindingWidth = 214;
        private const int Gap = 4;
        private readonly MenuRowButton actionButton;
        private readonly MenuRowButton bindingButton;
        private readonly ToolTip toolTip;
        private bool bindingCaptureActive;
        private bool bindingPointerInside;
        private bool suppressBindingHover;

        public CaptureActionMenuRow(
            string actionText,
            string bindingText,
            bool actionEnabled,
            bool bindingEnabled,
            string actionToolTip)
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.BackColor = SystemColors.Menu;
            this.Font = SystemFonts.MenuFont;
            this.Margin = Padding.Empty;
            this.Padding = Padding.Empty;
            this.TabStop = false;

            int measuredBindingWidth = TextRenderer.MeasureText(
                bindingText,
                this.Font,
                Size.Empty,
                TextFormatFlags.SingleLine).Width + 24;
            int bindingWidth = Math.Max(MinimumBindingWidth, measuredBindingWidth);
            this.Size = new Size(ActionWidth + Gap + bindingWidth, Math.Max(28, this.Font.Height + 12));

            this.actionButton = CreateButton(actionText, ContentAlignment.MiddleLeft, false);
            this.actionButton.Enabled = actionEnabled;
            this.actionButton.AccessibleName = actionText;
            this.actionButton.Click += delegate
            {
                EventHandler handler = this.ActionInvoked;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            AttachActionHighlighting(this.actionButton);
            this.Controls.Add(this.actionButton);

            this.bindingButton = CreateButton(bindingText, ContentAlignment.MiddleCenter, true);
            this.bindingButton.Enabled = bindingEnabled;
            this.bindingButton.AccessibleName = "Change " + actionText + " shortcut, currently " + bindingText;
            this.bindingButton.Click += delegate
            {
                EventHandler handler = this.RebindRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            AttachBindingHighlighting(this.bindingButton);
            this.Controls.Add(this.bindingButton);

            this.toolTip = new ToolTip();
            this.toolTip.Active = false;
            this.toolTip.SetToolTip(this.actionButton, actionToolTip);
            this.toolTip.SetToolTip(this.bindingButton, "Change shortcut - currently " + bindingText);
        }

        public event EventHandler ActionInvoked;
        public event EventHandler RebindRequested;

        internal string ActionText
        {
            get { return this.actionButton.Text; }
        }

        internal string BindingText
        {
            get { return this.bindingButton.Text; }
        }

        internal Rectangle ActionBounds
        {
            get { return this.actionButton.Bounds; }
        }

        internal Rectangle BindingBounds
        {
            get { return this.bindingButton.Bounds; }
        }

        /// <summary>
        /// True while the shortcut button wears the selected look. It must be false once a
        /// rebinding has been accepted, even though the pointer is still resting on the button the
        /// user clicked to start it.
        /// </summary>
        internal bool BindingHighlighted
        {
            get { return this.bindingButton.BackColor == SystemColors.Highlight; }
        }

        internal bool ActionHighlighted
        {
            get { return this.actionButton.BackColor == SystemColors.Highlight; }
        }

        internal Color ActionHoverBackColor
        {
            get { return this.actionButton.FlatAppearance.MouseOverBackColor; }
        }

        internal Color ActionTextColor
        {
            get { return this.actionButton.ForeColor; }
        }

        internal Color BindingHoverBackColor
        {
            get { return this.bindingButton.FlatAppearance.MouseOverBackColor; }
        }

        internal Color BindingTextColor
        {
            get { return this.bindingButton.ForeColor; }
        }

        internal void InvokeActionForTest()
        {
            this.actionButton.PerformClick();
        }

        internal void InvokeRebindForTest()
        {
            this.bindingButton.PerformClick();
        }

        internal void SimulateBindingPointerEnterForTest()
        {
            this.bindingButton.SimulateMouseEnter();
        }

        internal void SimulateActionPointerEnterForTest()
        {
            this.actionButton.SimulateMouseEnter();
        }

        internal void SimulateActionPointerLeaveForTest()
        {
            this.actionButton.SimulateMouseLeave();
        }

        internal void SimulateBindingPointerMoveForTest()
        {
            this.bindingButton.SimulateMouseMove();
        }

        internal void SimulateBindingPointerLeaveForTest()
        {
            this.bindingButton.SimulateMouseLeave();
        }

        internal void BeginBindingCapture()
        {
            this.bindingCaptureActive = true;
            this.suppressBindingHover = false;
            SetBindingText("Press shortcut…");
            ApplyBindingHighlight(true);
            this.bindingButton.FlatAppearance.BorderColor = SystemColors.Highlight;
        }

        internal void ShowPendingBinding(ShortcutBinding firstStroke, int secondsRemaining)
        {
            if (firstStroke == null)
            {
                throw new ArgumentNullException("firstStroke");
            }

            SetBindingText(firstStroke.ToDisplayString() + ", … " + secondsRemaining);
        }

        internal void CompleteBindingCapture(string bindingText)
        {
            this.bindingCaptureActive = false;
            this.suppressBindingHover = this.bindingPointerInside;
            SetBindingText(bindingText);
            ApplyBindingHighlight(false);
            this.bindingButton.FlatAppearance.BorderColor = SystemColors.ControlDark;
        }

        internal void SetBindingEnabled(bool enabled)
        {
            this.bindingButton.Enabled = enabled;
            if (!this.bindingCaptureActive)
            {
                this.bindingButton.ForeColor = enabled ? SystemColors.MenuText : SystemColors.GrayText;
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (this.actionButton == null || this.bindingButton == null)
            {
                return;
            }

            int scaledGap = Math.Max(2, (int)Math.Round(Gap * this.DeviceDpi / 96.0));
            int desiredBindingWidth = Math.Max(
                (int)Math.Round(MinimumBindingWidth * this.DeviceDpi / 96.0),
                TextRenderer.MeasureText(
                    this.bindingButton.Text,
                    this.Font,
                    Size.Empty,
                    TextFormatFlags.SingleLine).Width + scaledGap * 6);
            int reservedActionWidth = Math.Max(
                1,
                (int)Math.Round(ActionWidth * this.DeviceDpi / 96.0));
            int maximumBindingWidth = Math.Max(
                1,
                this.ClientSize.Width - reservedActionWidth - scaledGap);
            int bindingWidth = Math.Min(desiredBindingWidth, maximumBindingWidth);
            int actionWidth = Math.Max(1, this.ClientSize.Width - bindingWidth - scaledGap);

            this.actionButton.Bounds = new Rectangle(0, 0, actionWidth, this.ClientSize.Height);
            this.bindingButton.Bounds = new Rectangle(
                actionWidth + scaledGap,
                1,
                bindingWidth,
                Math.Max(1, this.ClientSize.Height - 2));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && this.toolTip != null)
            {
                this.toolTip.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SetBindingText(string text)
        {
            this.bindingButton.Text = text;
            this.bindingButton.AccessibleName = "Change " + this.actionButton.Text
                + " shortcut, currently " + text;
            if (this.toolTip != null)
            {
                this.toolTip.SetToolTip(this.bindingButton, "Change shortcut - currently " + text);
            }
            PerformLayout();
        }

        private void ApplyBindingHighlight(bool highlighted)
        {
            this.bindingButton.BackColor = highlighted ? SystemColors.Highlight : SystemColors.Menu;
            this.bindingButton.FlatAppearance.MouseOverBackColor = highlighted
                ? SystemColors.Highlight
                : SystemColors.Menu;
            if (highlighted)
            {
                this.bindingButton.ForeColor = SystemColors.HighlightText;
            }
            else
            {
                this.bindingButton.ForeColor = this.bindingButton.Enabled
                    ? SystemColors.MenuText
                    : SystemColors.GrayText;
            }
        }

        private void AttachActionHighlighting(MenuRowButton button)
        {
            button.FlatAppearance.MouseOverBackColor = SystemColors.Menu;
            button.MouseEnter += delegate
            {
                ApplyActionHighlight(true);
            };
            button.MouseLeave += delegate
            {
                ApplyActionHighlight(false);
            };
            button.Enter += delegate
            {
                ApplyActionHighlight(true);
            };
            button.Leave += delegate
            {
                ApplyActionHighlight(false);
            };
        }

        private void ApplyActionHighlight(bool highlighted)
        {
            this.actionButton.BackColor = highlighted ? SystemColors.Highlight : SystemColors.Menu;
            this.actionButton.FlatAppearance.MouseOverBackColor = highlighted
                ? SystemColors.Highlight
                : SystemColors.Menu;
            this.actionButton.ForeColor = highlighted
                ? SystemColors.HighlightText
                : (this.actionButton.Enabled ? SystemColors.MenuText : SystemColors.GrayText);
        }

        /// <summary>
        /// The shortcut button paints its own hover state so that accepting a rebinding can drop
        /// the selected look immediately. Windows' built-in flat hover colour would otherwise keep
        /// the button blue for as long as the pointer rests where the user clicked.
        /// </summary>
        private void AttachBindingHighlighting(MenuRowButton button)
        {
            button.FlatAppearance.MouseOverBackColor = SystemColors.Menu;
            button.MouseEnter += delegate
            {
                this.bindingPointerInside = true;
                if (!this.bindingCaptureActive && !this.suppressBindingHover)
                {
                    ApplyBindingHighlight(true);
                }
            };
            button.MouseMove += delegate
            {
                if (this.suppressBindingHover)
                {
                    this.suppressBindingHover = false;
                    if (!this.bindingCaptureActive)
                    {
                        ApplyBindingHighlight(true);
                    }
                }
            };
            button.MouseLeave += delegate
            {
                this.bindingPointerInside = false;
                this.suppressBindingHover = false;
                if (!this.bindingCaptureActive)
                {
                    ApplyBindingHighlight(false);
                }
            };
            button.Enter += delegate
            {
                if (!this.bindingCaptureActive && !this.suppressBindingHover)
                {
                    ApplyBindingHighlight(true);
                }
            };
            button.Leave += delegate
            {
                this.suppressBindingHover = false;
                if (!this.bindingCaptureActive)
                {
                    ApplyBindingHighlight(false);
                }
            };
        }

        private MenuRowButton CreateButton(string text, ContentAlignment alignment, bool drawBorder)
        {
            MenuRowButton button = new MenuRowButton();
            button.AutoEllipsis = true;
            button.BackColor = SystemColors.Menu;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = SystemColors.ControlDark;
            button.FlatAppearance.BorderSize = drawBorder ? 1 : 0;
            button.FlatAppearance.MouseDownBackColor = SystemColors.Highlight;
            button.ForeColor = SystemColors.MenuText;
            button.Margin = Padding.Empty;
            button.Padding = drawBorder ? Padding.Empty : new Padding(8, 0, 4, 0);
            button.TabStop = true;
            button.Text = text;
            button.TextAlign = alignment;
            button.UseMnemonic = false;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private sealed class MenuRowButton : Button
        {
            internal void SimulateMouseEnter()
            {
                OnMouseEnter(EventArgs.Empty);
            }

            internal void SimulateMouseMove()
            {
                OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, 1, 1, 0));
            }

            internal void SimulateMouseLeave()
            {
                OnMouseLeave(EventArgs.Empty);
            }
        }
    }
}
