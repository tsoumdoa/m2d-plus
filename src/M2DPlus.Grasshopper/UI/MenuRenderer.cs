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
