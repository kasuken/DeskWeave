using System.Windows;
using System.Windows.Threading;

namespace DeskWeave.Tests;

[CollectionDefinition("WPF")]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>;

public sealed class WpfFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly Application _app;

    public WpfFixture()
    {
        var ready = new TaskCompletionSource<Application>();
        _thread = new Thread(() =>
        {
            Application app;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/DeskWeave;component/Theme/NeonIntrusion.xaml", UriKind.Relative),
                });
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            ready.SetResult(app);
            app.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _app = ready.Task.GetAwaiter().GetResult();
    }

    public void Run(Action test) => _app.Dispatcher.Invoke(test, DispatcherPriority.Normal);

    public void Dispose()
    {
        _app.Dispatcher.Invoke(_app.Shutdown);
        _thread.Join();
    }
}
