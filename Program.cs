using System;
using System.Windows;

namespace TawmaeUIUpdater
{
    // ==============================================================
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(args.Length > 0 ? args[0] : null, Updater.Source);
            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    window.Crash(e.Exception);
                }
                catch (Exception)
                {
                    MessageBox.Show(e.Exception.Message, "TawmaeUI DLL Updater", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                }
            };
            return app.Run(window);
        }
    }
}
