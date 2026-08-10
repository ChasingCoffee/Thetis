//=================================================================
// VstRackContainerManager.cs
//=================================================================
// On-screen containers for the RX and TX VST chains.
//
// Thetis's container widgets (ucMeter, hosted floating in frmMeterDisplay) are
// not actually meter specific — neither type references MeterManager, clsMeter
// or MeterType. This reuses them to put a compact VST rack on the console that
// can be moved, resized, pinned and floated like a meter container.
//
// MeterManager is deliberately untouched. It owns meter creation, DirectX
// renderers, its own registry and the Setup UI, none of which apply here, and
// at ~44k lines it is not worth disturbing for this.
//
// Two things are needed to host a child control in a ucMeter, both discovered
// by spike and documented in .planning/VST_RACK_CONTAINERS_PLAN.md:
//   - the container reveals its move bar and resize grabber from mouse movement
//     over its own panel. A child control consumes those events, so the rack
//     relays them back via ucMeter.NotifyContentMouse*.
//   - the grabber lives in the bottom-right corner, where a scrolling child's
//     scrollbar sits. Scrollbars are non-client, so VstRackView also relays
//     WM_NCMOUSEMOVE.
//=================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    internal static class VstRackContainerManager
    {
        private const int RefreshIntervalMs = 750;

        private sealed class RackContainer
        {
            public VstChainKind Kind;
            public ucMeter Container;
            public frmMeterDisplay FloatingForm;
            public VstRackView RackView;
            public VstChainInfo LastInfo;
        }

        private static Console _console;
        private static RackContainer _rx;
        private static RackContainer _tx;
        private static System.Windows.Forms.Timer _refreshTimer;
        private static bool _restoring;

        /// <summary>
        /// Called once during startup, only when VST hosting is enabled.
        /// Recreates whichever containers were visible when Thetis last closed.
        /// </summary>
        public static void Initialize(Console console)
        {
            if (console == null || !Console.VstEnabled)
                return;

            _console = console;

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = RefreshIntervalMs;
            _refreshTimer.Tick += OnRefreshTick;

            _restoring = true;

            try
            {
                if (VstHost.UiState.RxContainerVisible)
                    ShowContainer(VstChainKind.Rx);
                if (VstHost.UiState.TxContainerVisible)
                    ShowContainer(VstChainKind.Tx);
            }
            finally
            {
                _restoring = false;
            }
        }

        public static bool IsVisible(VstChainKind kind)
        {
            RackContainer rc = Get(kind);

            return rc != null && rc.Container != null && !rc.Container.IsDisposed;
        }

        public static void ToggleContainer(VstChainKind kind)
        {
            if (IsVisible(kind))
                HideContainer(kind);
            else
                ShowContainer(kind);
        }

        public static void ShowContainer(VstChainKind kind)
        {
            if (_console == null || !Console.VstEnabled)
                return;

            if (IsVisible(kind))
            {
                Get(kind).Container.BringToFront();
                return;
            }

            RackContainer rc = new RackContainer();
            rc.Kind = kind;

            rc.RackView = new VstRackView(kind);
            rc.RackView.Dock = DockStyle.Fill;
            rc.RackView.Compact = true;

            WireRackEvents(rc);

            rc.Container = new ucMeter();
            rc.Container.ID = ContainerId(kind);
            rc.Container.RX = 1;
            rc.Container.Console = _console;
            rc.Container.UCBorder = true;
            rc.Container.MeterEnabled = true;
            rc.Container.AutoHeight = false;
            rc.Container.Notes = VstHost.GetChainDisplayName(kind) + " VST rack";

            rc.Container.DisplayContainer.Controls.Add(rc.RackView);

            // Relay content mouse activity so the container still shows its move
            // bar and resize grabber (see file header).
            rc.RackView.ContentMouseMove += delegate { SafeNotifyMouseMove(rc); };
            rc.RackView.ContentMouseLeave += delegate { SafeNotifyMouseLeave(rc); };

            rc.FloatingForm = new frmMeterDisplay(_console, 1);
            rc.FloatingForm.ID = rc.Container.ID;
            rc.FloatingForm.FormEnabled = true;

            ApplyStoredGeometry(rc);

            rc.Container.FloatingDockedClicked += delegate { OnFloatDockToggled(rc); };
            rc.Container.DockedMoved += delegate { PersistGeometry(rc); };
            rc.Container.Resize += delegate { PersistGeometry(rc); };

            Set(kind, rc);

            if (rc.Container.Floating)
            {
                // Restored in the floating state: hand it straight to its form.
                rc.FloatingForm.TakeOwner(rc.Container);
                rc.FloatingForm.Floating = true;
                rc.Container.Floating = true;
                rc.FloatingForm.Show();
            }
            else
            {
                rc.Container.Parent = _console;
                rc.Container.Anchor = AnchorStyles.None;
                rc.Container.BringToFront();
                rc.Container.Show();
            }

            RefreshContainer(rc);

            if (!_restoring)
            {
                SetVisibleFlag(kind, true);
                PersistGeometry(rc);
            }

            EnsureTimerState();
        }

        public static void HideContainer(VstChainKind kind)
        {
            RackContainer rc = Get(kind);

            if (rc == null)
                return;

            PersistGeometry(rc);
            DisposeContainer(rc);
            Set(kind, null);

            if (!_restoring)
                SetVisibleFlag(kind, false);

            EnsureTimerState();
        }

        /// <summary>Tears both containers down, e.g. during shutdown.</summary>
        public static void Shutdown()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Tick -= OnRefreshTick;
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }

            if (_rx != null) { PersistGeometry(_rx); DisposeContainer(_rx); _rx = null; }
            if (_tx != null) { PersistGeometry(_tx); DisposeContainer(_tx); _tx = null; }
        }

        #region Wiring

        private static void WireRackEvents(RackContainer rc)
        {
            rc.RackView.EditorRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                VstPluginState plugin = PluginAt(rc, e.Index);

                if (plugin == null || plugin.LoadState != VstPluginLoadState.Active)
                    return;

                // The bridge call can block briefly; keep it off the UI thread.
                VstChainKind kind = rc.Kind;
                int index = e.Index;

                System.Threading.Tasks.Task.Run(() => VstHost.OpenPluginEditorWindow(kind, index));
            };

            rc.RackView.EnabledToggleRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                VstPluginState plugin = PluginAt(rc, e.Index);

                if (plugin != null && VstHost.SetPluginEnabled(rc.Kind, e.Index, !plugin.Enabled))
                    RefreshContainer(rc);
            };

            rc.RackView.BypassToggleRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                VstPluginState plugin = PluginAt(rc, e.Index);

                if (plugin != null && VstHost.SetPluginBypass(rc.Kind, e.Index, !plugin.Bypass))
                    RefreshContainer(rc);
            };

            rc.RackView.RemoveRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                if (VstHost.RemovePlugin(rc.Kind, e.Index))
                    RefreshContainer(rc);
            };

            rc.RackView.MoveRequested += delegate(object s, VstRackMoveEventArgs e)
            {
                if (VstHost.MovePlugin(rc.Kind, e.Index, e.Index + e.Delta))
                    RefreshContainer(rc);
            };

            // Adding a plugin needs the picker, which belongs to the chain
            // manager window; send the user there rather than duplicating it.
            rc.RackView.AddRequested += delegate
            {
                if (_console != null)
                {
                    _console.VstChainManagerForm.RefreshChains();
                    _console.VstChainManagerForm.Show(_console);
                    _console.VstChainManagerForm.Focus();
                }
            };
        }

        private static void SafeNotifyMouseMove(RackContainer rc)
        {
            if (rc.Container != null && !rc.Container.IsDisposed)
                rc.Container.NotifyContentMouseMove();
        }

        private static void SafeNotifyMouseLeave(RackContainer rc)
        {
            if (rc.Container != null && !rc.Container.IsDisposed)
                rc.Container.NotifyContentMouseLeave();
        }

        // Mirrors MeterManager.setMeterFloating / returnMeterFromFloating.
        private static void OnFloatDockToggled(RackContainer rc)
        {
            if (_console == null || rc.Container == null || rc.FloatingForm == null)
                return;

            if (rc.Container.Floating)
            {
                rc.FloatingForm.Floating = false;
                rc.FloatingForm.Hide();
                rc.Container.Hide();
                rc.Container.Parent = _console;
                rc.Container.Anchor = AnchorStyles.None;
                rc.Container.RestoreLocation();
                rc.Container.Floating = false;
                rc.Container.BringToFront();
                rc.Container.Show();
            }
            else
            {
                rc.Container.Hide();
                rc.Container.Repaint();
                rc.FloatingForm.TakeOwner(rc.Container);
                rc.FloatingForm.Floating = true;
                rc.Container.Floating = true;
                rc.FloatingForm.Show();
            }

            PersistGeometry(rc);
        }

        #endregion

        #region Refresh

        private static void EnsureTimerState()
        {
            if (_refreshTimer == null)
                return;

            bool anyVisible = IsVisible(VstChainKind.Rx) || IsVisible(VstChainKind.Tx);

            if (anyVisible && !_refreshTimer.Enabled)
                _refreshTimer.Start();
            else if (!anyVisible && _refreshTimer.Enabled)
                _refreshTimer.Stop();
        }

        private static void OnRefreshTick(object sender, EventArgs e)
        {
            RefreshContainer(_rx);
            RefreshContainer(_tx);
        }

        private static void RefreshContainer(RackContainer rc)
        {
            if (rc == null || rc.RackView == null || rc.RackView.IsDisposed)
                return;

            VstChainInfo info = VstHost.GetChainInfo(rc.Kind);

            if (info == null)
                return;

            rc.LastInfo = info;
            rc.RackView.SetPlugins(info.Plugins, 16);
        }

        private static VstPluginState PluginAt(RackContainer rc, int index)
        {
            if (rc.LastInfo == null || rc.LastInfo.Plugins == null)
                return null;
            if (index < 0 || index >= rc.LastInfo.Plugins.Count)
                return null;

            return rc.LastInfo.Plugins[index];
        }

        #endregion

        #region Persistence

        private static void ApplyStoredGeometry(RackContainer rc)
        {
            string stored = rc.Kind == VstChainKind.Rx
                ? VstHost.UiState.RxContainerData
                : VstHost.UiState.TxContainerData;

            bool restored = false;

            if (!string.IsNullOrWhiteSpace(stored))
            {
                try
                {
                    restored = rc.Container.TryParse(stored);
                }
                catch
                {
                    restored = false;
                }
            }

            if (!restored)
            {
                rc.Container.Size = new Size(360, 220);
                rc.Container.DockedSize = rc.Container.Size;
                rc.Container.DockedLocation = rc.Kind == VstChainKind.Rx
                    ? new Point(60, 120)
                    : new Point(60, 360);
            }

            // TryParse restores the stored ID; keep ours so the two containers
            // stay distinguishable.
            rc.Container.ID = ContainerId(rc.Kind);
            rc.FloatingForm.ID = rc.Container.ID;
            rc.Container.Location = rc.Container.DockedLocation;

            if (rc.Container.Size.Width < ucMeter.MIN_CONTAINER_WIDTH ||
                rc.Container.Size.Height < ucMeter.MIN_CONTAINER_HEIGHT)
            {
                rc.Container.Size = new Size(360, 220);
                rc.Container.DockedSize = rc.Container.Size;
            }
        }

        private static void PersistGeometry(RackContainer rc)
        {
            if (rc == null || rc.Container == null || rc.Container.IsDisposed || _restoring)
                return;

            string data = rc.Container.ToString();

            if (rc.Kind == VstChainKind.Rx)
            {
                if (VstHost.UiState.RxContainerData == data)
                    return;

                VstHost.UiState.RxContainerData = data;
            }
            else
            {
                if (VstHost.UiState.TxContainerData == data)
                    return;

                VstHost.UiState.TxContainerData = data;
            }

            VstHost.ScheduleUiStateSave();
        }

        private static void SetVisibleFlag(VstChainKind kind, bool visible)
        {
            if (kind == VstChainKind.Rx)
                VstHost.UiState.RxContainerVisible = visible;
            else
                VstHost.UiState.TxContainerVisible = visible;

            VstHost.ScheduleUiStateSave();
        }

        #endregion

        #region Plumbing

        private static string ContainerId(VstChainKind kind)
        {
            return kind == VstChainKind.Rx ? "vst-rack-rx" : "vst-rack-tx";
        }

        private static RackContainer Get(VstChainKind kind)
        {
            return kind == VstChainKind.Rx ? _rx : _tx;
        }

        private static void Set(VstChainKind kind, RackContainer rc)
        {
            if (kind == VstChainKind.Rx)
                _rx = rc;
            else
                _tx = rc;
        }

        private static void DisposeContainer(RackContainer rc)
        {
            if (rc == null)
                return;

            if (rc.Container != null && !rc.Container.IsDisposed)
            {
                rc.Container.RemoveDelegates();
                rc.Container.Hide();
                rc.Container.Parent = null;
                rc.Container.Dispose();
            }

            if (rc.FloatingForm != null && !rc.FloatingForm.IsDisposed)
            {
                rc.FloatingForm.Hide();
                rc.FloatingForm.Dispose();
            }

            rc.Container = null;
            rc.FloatingForm = null;
            rc.RackView = null;
        }

        #endregion
    }
}
