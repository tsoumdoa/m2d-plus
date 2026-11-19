using System.Windows.Forms;
using Grasshopper.Kernel;
using M2DPlus.Grasshopper.UI.Model;

namespace M2DPlus.Grasshopper.UI;

/// <summary>Turns toolkit-free MenuNodes into GH context-menu items. The handler receives the command and
/// whether Shift was held at the click (Bake: add as a copy).</summary>
internal static class MenuRenderer
{
    public static void Append(ToolStripDropDown menu, IEnumerable<MenuNode> nodes, Action<string, bool> execute)
    {
        foreach (var node in nodes) Add(menu, node, execute);
    }

    // WinForms treats a single & as a mnemonic marker ("View range & settings…").
    private static string Escape(string label) => label.Replace("&", "&&", StringComparison.Ordinal);

    private static void Add(ToolStrip menu, MenuNode node, Action<string, bool> execute)
    {
        if (node.IsSeparator) { GH_DocumentObject.Menu_AppendSeparator(menu); return; }
        if (node.TextEntry is { } entry && menu is ToolStripDropDown dropDown)
        {
            GH_DocumentObject.Menu_AppendSeparator(dropDown);
            GH_DocumentObject.Menu_AppendItem(dropDown, node.Label + " (document units; Enter)", null, false, false);
            var error = new ToolStripLabel { ForeColor = System.Drawing.Color.FromArgb(0xC4, 0x2B, 0x1C), Visible = false };
            var box = GH_DocumentObject.Menu_AppendTextItem(dropDown, entry.Text, (sender, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true; e.SuppressKeyPress = true;
                if (!entry.TryAccept(sender.Text, out var command, out var problem))
                { error.Text = problem; error.Visible = true; return; }
                sender.CloseEntireMenuStructure();
                execute(command!, false);
            }, (_, _) => error.Visible = false, node.Enabled, 240, false);
            box.ToolTipText = node.Hint;
            dropDown.Items.Add(error);
            return;
        }
        ToolStripMenuItem item;
        if (node.IsSubmenu)
        {
            item = GH_DocumentObject.Menu_AppendItem(menu, Escape(node.Label));
            item.Enabled = node.Enabled;
            foreach (var child in node.Children) Add(item.DropDown, child, execute);
        }
        else
        {
            var command = node.Command;
            item = GH_DocumentObject.Menu_AppendItem(menu, Escape(node.Label),
                command is null ? null : (_, _) => execute(command, (Control.ModifierKeys & Keys.Shift) != 0),
                node.Enabled && command is not null, node.Checked);
        }
        if (node.Hint is not null) item.ShortcutKeyDisplayString = node.Hint;
        if (node.Tooltip is not null) item.ToolTipText = node.Tooltip;
    }
}
