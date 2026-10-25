using System;
using System.Drawing;
using System.Windows.Forms;

namespace SlideKeyboard.App.Services;

/// <summary>Notification-area icon: show/hide, settings, exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;

    public TrayIcon(Action toggle, Action settings, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / hide keyboard   Ctrl+Alt+K", null, (_, _) => toggle());
        menu.Items.Add("Settings", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Text = "Slide Keyboard",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) toggle();
        };
    }

    static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info != null) return new Icon(info.Stream, SystemInformation.SmallIconSize);
        }
        catch
        {
            // fall through
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
