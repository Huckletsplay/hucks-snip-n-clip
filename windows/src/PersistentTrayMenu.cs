using System;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class PersistentTrayMenu : ContextMenuStrip
    {
        internal bool PreserveInteraction;

        internal void Configure()
        {
            ConfigureDropDown(this);
        }

        private void ConfigureDropDown(ToolStripDropDown dropDown)
        {
            dropDown.ShowItemToolTips = false;
            dropDown.ItemClicked += delegate(object sender, ToolStripItemClickedEventArgs e)
            {
                if (e.ClickedItem is PersistentChoice) PreserveInteraction = true;
            };
            dropDown.Closing += delegate(object sender, ToolStripDropDownClosingEventArgs e)
            {
                if (PreserveInteraction && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                    e.Cancel = true;
            };
            foreach (ToolStripItem item in dropDown.Items)
            {
                ToolStripMenuItem branch = item as ToolStripMenuItem;
                if (branch != null && branch.HasDropDownItems) ConfigureDropDown(branch.DropDown);
            }
        }

        internal static void CopyState(ToolStripItemCollection source, ToolStripItemCollection target)
        {
            if (source.Count != target.Count) throw new InvalidOperationException("A routine setting changed the menu structure.");
            for (int i = 0; i < source.Count; i++)
            {
                target[i].Text = source[i].Text;
                target[i].Enabled = source[i].Enabled;
                ToolStripMenuItem from = source[i] as ToolStripMenuItem;
                ToolStripMenuItem to = target[i] as ToolStripMenuItem;
                if (from != null && to != null)
                {
                    to.Checked = from.Checked;
                    CopyState(from.DropDownItems, to.DropDownItems);
                }
            }
        }
    }

    internal sealed class PersistentChoice : ToolStripMenuItem
    {
        public PersistentChoice(string text) : base(text) { }
        protected override bool DismissWhenClicked { get { return false; } }

        protected override void OnClick(EventArgs e)
        {
            ToolStrip current = this.Owner;
            while (current is ToolStripDropDown && !(current is PersistentTrayMenu))
            {
                ToolStripItem parent = ((ToolStripDropDown)current).OwnerItem;
                current = parent == null ? null : parent.Owner;
            }
            PersistentTrayMenu root = current as PersistentTrayMenu;
            if (root == null) { base.OnClick(e); return; }
            root.PreserveInteraction = true;
            try { base.OnClick(e); }
            finally
            {
                if (root.IsHandleCreated && !root.IsDisposed)
                    root.BeginInvoke(new Action(delegate { root.PreserveInteraction = false; }));
                else root.PreserveInteraction = false;
            }
        }
    }
}
