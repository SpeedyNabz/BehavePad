using System.Drawing;
using Forms = System.Windows.Forms;

namespace BehavePad.Services;

/// <summary>Keeps BehavePad reachable from the notification area while it filters in the background.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripItem _toggleItem;

    public TrayIcon(Action open, Func<Task> toggleFilter, Action exit)
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "BehavePad",
            Visible = true,
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open BehavePad", null, (_, _) => open());
        _toggleItem = menu.Items.Add("Turn filter on", null, async (_, _) => await toggleFilter());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit BehavePad", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                open();
            }
        };
    }

    public void Update(bool filterOn)
    {
        _toggleItem.Text = filterOn ? "Turn filter off" : "Turn filter on";
        _icon.Text = filterOn ? "BehavePad: filter on" : "BehavePad: filter off";
    }

    public void ShowNotice(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/BehavePad.ico"));
        if (resource is null)
        {
            return SystemIcons.Application;
        }

        using var stream = resource.Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
