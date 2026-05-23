using System;
using System.Net;
using Gtk;
using TraceLog;
using Translations;
using XDM.Core;
using XDM.Core.DataAccess;
using XDMApp = XDM.Core.Application;
using System.Linq;
using XDM.Core.BrowserMonitoring;
using XDM.Core.Util;

namespace XDM.GtkUI
{
    class Program
    {
        private const string DisableCachingName = @"TestSwitch.LocalAppContext.DisableCaching";
        private const string DontEnableSchUseStrongCryptoName = @"Switch.System.Net.DontEnableSchUseStrongCrypto";

        static void Main(string[] args)
        {
            Config.LoadConfig();
            var debugMode = Environment.GetEnvironmentVariable("XDM_DEBUG_MODE");
            if (!string.IsNullOrEmpty(debugMode) && debugMode == "1")
            {
                var logFile = System.IO.Path.Combine(Config.AppDir, "log.txt");
                Log.InitFileBasedTrace(System.IO.Path.Combine(Config.AppDir, "log.txt"));
            }
            Log.Debug("Application_Startup");
            Environment.SetEnvironmentVariable("GTK_USE_PORTAL", "1");
            Gtk.Application.Init("xdm-app", ref args);
            GLib.ExceptionManager.UnhandledException += ExceptionManager_UnhandledException;
            var globalStyleSheet = @"
                /* === XDM Branded GTK Theme === */

                /* Font size classes */
                .large-font { font-size: 16px; }
                .medium-font { font-size: 14px; font-weight: 600; }

                /* Accent color for selections and focus */
                @define-color accent_color #6c8cff;
                @define-color accent_hover #8aa4ff;

                /* Progress bars — branded gradient */
                progressbar trough {
                    min-height: 6px;
                    border-radius: 3px;
                    background: alpha(@theme_fg_color, 0.1);
                }
                progressbar progress {
                    min-height: 6px;
                    border-radius: 3px;
                    background: linear-gradient(to right, #6c8cff, #4ecdc4);
                }

                /* TreeView selection */
                treeview.view:selected {
                    background-color: @accent_color;
                    color: white;
                }
                treeview.view:hover {
                    background-color: alpha(@accent_color, 0.1);
                }

                /* Buttons — subtle rounded style */
                button {
                    border-radius: 6px;
                    padding: 4px 12px;
                    transition: 200ms ease;
                }
                button:hover {
                    background-image: none;
                    background-color: alpha(@accent_color, 0.12);
                }
                button.suggested-action {
                    background-color: @accent_color;
                    color: white;
                }

                /* Notebook tabs */
                notebook tab {
                    padding: 6px 14px;
                    border-radius: 6px 6px 0 0;
                }
                notebook tab:checked {
                    background-color: alpha(@accent_color, 0.15);
                }

                /* Entry fields */
                entry {
                    border-radius: 6px;
                    padding: 6px 8px;
                }
                entry:focus {
                    border-color: @accent_color;
                    box-shadow: 0 0 0 2px alpha(@accent_color, 0.2);
                }

                /* Category sidebar */
                .dark {
                    color: @theme_fg_color;
                    background: alpha(@theme_fg_color, 0.04);
                }

                /* Scrollbar styling */
                scrollbar slider {
                    min-width: 6px;
                    min-height: 6px;
                    border-radius: 3px;
                    background-color: alpha(@theme_fg_color, 0.2);
                }
                scrollbar slider:hover {
                    background-color: alpha(@theme_fg_color, 0.35);
                }

                /* Check buttons */
                checkbutton check {
                    border-radius: 4px;
                }
                checkbutton check:checked {
                    background-color: @accent_color;
                    border-color: @accent_color;
                }

                /* Spin buttons */
                spinbutton {
                    border-radius: 6px;
                }
            ";

            var screen = Gdk.Screen.Default;
            var provider = new CssProvider();
            provider.LoadFromData(globalStyleSheet);
            Gtk.StyleContext.AddProviderForScreen(screen, provider, 800);

            // .NET 10 uses HttpClient which handles TLS and connection limits internally.
            // ServicePointManager is deprecated and has no effect on modern HttpClient.

            Log.Debug("Loading languages...");

            LoadLanguageTexts();

            if (Config.Instance.AllowSystemDarkTheme)
            {
                Gtk.Settings.Default.ThemeName = "Adwaita";
                Gtk.Settings.Default.ApplicationPreferDarkTheme = true;
            }

            var core = new ApplicationCore();
            var app = new XDMApp();
            var win = new MainWindow();

            Log.Debug("Configuring app context...");

            ApplicationContext.FirstRunCallback += ApplicationContext_FirstRunCallback;
            ApplicationContext.Configurer()
                .RegisterApplicationWindow(win)
                .RegisterApplication(app)
                .RegisterApplicationCore(core)
                .RegisterCapturedVideoTracker(new VideoTracker())
                .RegisterClipboardMonitor(new ClipboardMonitor())
                .RegisterLinkRefresher(new LinkRefresher())
                .RegisterPlatformUIService(new GtkPlatformUIService())
                .Configure();

            Log.Debug("Processing arguments...");

            ArgsProcessor.Process(args);

            Log.Debug("Gtk Run...");

            Gtk.Application.Run();
        }

        private static void ApplicationContext_FirstRunCallback(object? sender, EventArgs e)
        {
            PlatformHelper.EnableAutoStart(true);
        }

        private static void ExceptionManager_UnhandledException(GLib.UnhandledExceptionArgs args)
        {
            Log.Debug("GLib ExceptionManager_UnhandledException: " + args.ExceptionObject);
            args.ExitApplication = false;
        }

        private static void LoadLanguageTexts()
        {
            Log.Debug("Language loading ...");
            try
            {
                var indexFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lang", "index.txt");
                if (System.IO.File.Exists(indexFile))
                {
                    var lines = System.IO.File.ReadAllLines(indexFile);
                    foreach (var line in lines)
                    {
                        var index = line.IndexOf("=");
                        if (index > 0)
                        {
                            var name = line.Substring(0, index);
                            var value = line.Substring(index + 1);
                            if (name == Config.Instance.Language)
                            {
                                TextResource.Load(value);
                                break;
                            }
                        }
                    }
                }
                Log.Debug("Language loaded.");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
        }
    }
}
