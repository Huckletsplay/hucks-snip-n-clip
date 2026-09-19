using System;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class DestinationNameForm : Form
    {
        private readonly TextBox nameTextBox;

        public DestinationNameForm(string suggestedName)
        {
            this.Text = "Name Destination";
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(390, 132);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true;

            Label label = new Label();
            label.AutoSize = true;
            label.Location = new Point(16, 16);
            label.Text = "What should Huck’s Snip ’n’ Clip call this destination?";

            this.nameTextBox = new TextBox();
            this.nameTextBox.Location = new Point(19, 43);
            this.nameTextBox.Size = new Size(352, 23);
            this.nameTextBox.Text = suggestedName ?? String.Empty;
            this.nameTextBox.SelectAll();

            Button cancelButton = new Button();
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(215, 87);
            cancelButton.Size = new Size(75, 27);
            cancelButton.Text = "Cancel";

            Button saveButton = new Button();
            saveButton.Location = new Point(296, 87);
            saveButton.Size = new Size(75, 27);
            saveButton.Text = "Add";
            saveButton.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(this.nameTextBox.Text))
                {
                    System.Media.SystemSounds.Beep.Play();
                    this.nameTextBox.Focus();
                    return;
                }

                this.DialogResult = DialogResult.OK;
                Close();
            };

            this.AcceptButton = saveButton;
            this.CancelButton = cancelButton;
            this.Controls.Add(label);
            this.Controls.Add(this.nameTextBox);
            this.Controls.Add(cancelButton);
            this.Controls.Add(saveButton);
        }

        public string DestinationName
        {
            get { return this.nameTextBox.Text.Trim(); }
        }
    }
}
