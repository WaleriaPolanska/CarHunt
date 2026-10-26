using System;
using System.IO;
using System.Threading.Tasks;
using AsyncImageLoader;
using Avalonia.Threading;
using CarHunt.Services.Diagnostics;
using CarHunt.Services.Images;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using CarHunt.ViewModels;
using CarHunt.Views;

namespace CarHunt;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SetUpImageCache();
        CatchFailures();
    }

    /// <summary>
    /// Writes down why the app is about to die, and then lets it.
    ///
    /// Deliberately not swallowed: Play Console only ever receives crashes and ANRs, so
    /// an error caught and hidden here is an error nobody will ever hear about. Letting
    /// it through costs the user a "CarHunt keeps stopping" box and buys a stack trace in
    /// the console, which is the only way a bug in someone else's hands reaches us.
    ///
    /// The log line is written first, since it holds the part Play never shows — which
    /// site was being read, and what the app had been doing.
    /// </summary>
    private static void CatchFailures()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            AppLog.Write($"сбой в интерфейсе: {e.Exception.GetType().Name} {e.Exception.Message}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write($"падение: {(e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject.ToString()}");

        // A faulted background task that nobody awaited: logged and marked observed,
        // because leaving it unobserved brings the process down at a moment that has
        // nothing to do with the cause, and the trace Play would receive is useless.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"необработанная задача: {e.Exception.GetType().Name} {e.Exception.Message}");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Listing photos are cached on disk instead of in memory.
    ///
    /// The library's default, RamCachedWebImageLoader, keeps every decoded bitmap in an
    /// unbounded dictionary for the lifetime of the process: scroll through a few
    /// searches and every photo ever shown is still resident. A phone feels that first.
    /// On disk they cost nothing at rest and survive a restart, so a repeated search
    /// draws its photos without downloading them again.
    /// </summary>
    private static void SetUpImageCache()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "carhunt-images");
        Directory.CreateDirectory(folder);
        ImageLoader.AsyncImageLoader = new CardImageLoader(folder + Path.DirectorySeparatorChar);

        // Nothing else ever deletes these: drop what has not been touched in a week, then
        // hold the folder under 60 MB — a full search is roughly 4,000 photos at 15 KB,
        // so without a ceiling a few of them would fill a phone. Off the startup path:
        // it is disk work, and nothing waits on it.
        Task.Run(() =>
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var file in Directory.EnumerateFiles(folder))
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            CardImageLoader.TrimCache(folder, 60L * 1024 * 1024);
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}