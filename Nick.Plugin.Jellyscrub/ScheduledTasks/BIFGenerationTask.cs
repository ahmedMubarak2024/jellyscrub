using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Nick.Plugin.Jellyscrub.Drawing;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Configuration;
using Nick.Plugin.Jellyscrub.Configuration;

namespace Nick.Plugin.Jellyscrub.ScheduledTasks;

/// <summary>
/// Class BIFGenerationTask.
/// </summary>
public class BIFGenerationTask : IScheduledTask
{
    private readonly ILogger<BIFGenerationTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILocalizationManager _localization;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly PluginConfiguration _config;
    public static int usingCpuResource = 0;


    public BIFGenerationTask(
        ILibraryManager libraryManager,
        ILogger<BIFGenerationTask> logger,
        ILoggerFactory loggerFactory,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        ILibraryMonitor libraryMonitor,
        ILocalizationManager localization,
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager configurationManager,
        EncodingHelper encodingHelper)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _libraryMonitor = libraryMonitor;
        _localization = localization;
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _encodingHelper = encodingHelper;
        _config = JellyscrubPlugin.Instance!.Configuration;
    }

    /// <inheritdoc />
    public string Name => "Generate BIF Files";

    /// <inheritdoc />
    public string Key => "GenerateBIFFiles";

    /// <inheritdoc />
    public string Description => "Generates BIF files to be used for jellyscrub scrubbing preview.";

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        OldMediaEncoder._cpuProccessList.Clear();
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            IsVirtualItem = false,
            Recursive = true

        }).OfType<Video>().ToList();

        var numComplete = 0;
        var tasks = new List<Task>();
        usingCpuResource = 0;

        SemaphoreSlim concurrencySemaphore = new SemaphoreSlim(_config.ParrelWorkCountInput, _config.ParrelWorkCountInput);


        foreach (var item in items)
        {
            tasks.Add(
                Task.Run(async () =>
                {
                    await concurrencySemaphore.WaitAsync(cancellationToken);
                    var isCreated = VideoProcessor.EnableForItem(item, _fileSystem, _config.Interval);
                    try
                    {

                        if (isCreated)
                            _logger.LogInformation(item.Name + " Started");


                        cancellationToken.ThrowIfCancellationRequested();


                        await new VideoProcessor(_loggerFactory, _loggerFactory.CreateLogger<VideoProcessor>(), _mediaEncoder, _configurationManager, _fileSystem, _appPaths, _libraryMonitor, _encodingHelper)
                            .Run(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Error creating trickplay files for {0}: {1}", item.Name, ex);
                    }

                    numComplete++;
                    double percent = numComplete;
                    percent /= items.Count;
                    percent *= 100;
                    if (isCreated)
                        _logger.LogInformation(item.Name + " completed successfully index:" + items.IndexOf(item));

                    progress.Report(percent);
                    concurrencySemaphore.Release();
                }, cancellationToken)
            );



        }
        await Task.WhenAll(tasks);
    }
}
