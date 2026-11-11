using System;
using System.Drawing;
using DdoDiscordPresence.UI;
using VoK.Sdk.Plugins;

namespace DdoDiscordPresence
{
    internal sealed class PluginUI : IPluginUI, IDisposable
    {
        private readonly Image _icon;
        private readonly SettingsForm _form;

        public PluginUI(Plugin plugin)
        {
            // Drawn first: Dungeon Helper reads ToolbarImage while plugins start.
            _icon = PresenceIcon.DrawToolbarIcon();
            _form = new SettingsForm(plugin);
        }

        internal SettingsForm Form => _form;

        public float? FocusedOpacity => 1.0f;
        public bool EnabledInCharacterSelection => true;
        // The same cached bitmap every time: Dungeon Helper reads it on every toolbar redraw.
        public Image ToolbarImage => _icon;
        public object UserInterfaceForm => _form;
        public Tuple<int, int> MinSize => new Tuple<int, int>(560, 680);

        public void Dispose()
        {
            try { if (!_form.IsDisposed) _form.Dispose(); } catch { }
        }
    }
}
