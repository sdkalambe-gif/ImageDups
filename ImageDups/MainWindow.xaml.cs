using Microsoft.VisualBasic.FileIO;
using log4net;
using log4net.Appender;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace ImageDups
{
    public partial class MainWindow : Window
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff"
        };
        private const int PerceptualHashSize = 32;
        private const int LowFrequencySize = 8;
        private const int PerceptualHashBitCount = LowFrequencySize * LowFrequencySize;
        private const int MinimumPerceptualSimilarity = 90;
        private const int MaximumPerceptualSimilarity = 100;
        private const int EtaAverageUpdateInterval = 30;
        private const string ComparingImagesStage = "Comparing SHA-256, byte content and pHash";
        private const string IndexCacheFileName = ".imagedups-index-v2.cache";
        private static readonly ILog Logger = LogManager.GetLogger(typeof(MainWindow));
        private static readonly double[,] DctCosines = CreateDctCosines();

        public ObservableCollection<ImageMatch> Matches { get; private set; }
        private bool isComparing;
        private bool isIndexing;
        private bool isIndexingPaused;
        private int masterImagesAddedToCache;
        private int currentImagesAddedToCache;
        private int nextEtaAverageUpdate = EtaAverageUpdateInterval;
        private double? etaAverageSecondsPerImage;
        private string etaEstimateStage;
        private readonly ManualResetEventSlim indexingPause = new ManualResetEventSlim(true);
        private int byteSimilarityThreshold = 96;
        private int perceptualSimilarityThreshold = 100;

        public MainWindow()
        {
            InitializeComponent();
            ConfigureLogging();
            Matches = new ObservableCollection<ImageMatch>();
            DataContext = this;
            ByteThresholdTextBlock.Text = byteSimilarityThreshold + "%";
            PHashThresholdTextBlock.Text = perceptualSimilarityThreshold + "%";
            string savedMasterFolder = Properties.Settings.Default.MasterFolderPath;
            if (Directory.Exists(savedMasterFolder))
            {
                MasterFolderTextBox.Text = savedMasterFolder;
            }

            string savedCurrentFolder = Properties.Settings.Default.CurrentFolderPath;
            if (Directory.Exists(savedCurrentFolder))
            {
                CurrentFolderTextBox.Text = savedCurrentFolder;
            }

            UpdateCacheStatus();
            UpdateThresholdControlState();
            string applicationVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .Cast<AssemblyInformationalVersionAttribute>()
                .Single()
                .InformationalVersion;
            Logger.Info("Application started. Version: " + applicationVersion + ".");
        }

        private void ChooseMasterFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = ChooseFolder(
                "Select the folder containing your master images.",
                MasterFolderTextBox.Text);
            if (folder != null)
            {
                MasterFolderTextBox.Text = folder;
                Properties.Settings.Default.MasterFolderPath = folder;
                Properties.Settings.Default.Save();
                masterImagesAddedToCache = 0;
                currentImagesAddedToCache = 0;
                UpdateCacheStatus();
                Logger.Info("Master folder selected: " + folder);
                ClearResults();
            }
        }

        private void ChooseCurrentFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = ChooseFolder(
                "Select the folder containing the images to compare.",
                CurrentFolderTextBox.Text);
            if (folder != null)
            {
                CurrentFolderTextBox.Text = folder;
                Properties.Settings.Default.CurrentFolderPath = folder;
                Properties.Settings.Default.Save();
                currentImagesAddedToCache = 0;
                UpdateCacheStatus();
                Logger.Info("Current-images folder selected: " + folder);
                ClearResults();
            }
        }

        private void DecreaseThreshold_Click(object sender, RoutedEventArgs e)
        {
            SetByteSimilarityThreshold(byteSimilarityThreshold - 1);
        }

        private void IncreaseThreshold_Click(object sender, RoutedEventArgs e)
        {
            SetByteSimilarityThreshold(byteSimilarityThreshold + 1);
        }

        private void DecreasePHashThreshold_Click(object sender, RoutedEventArgs e)
        {
            SetPerceptualSimilarityThreshold(perceptualSimilarityThreshold - 1);
        }

        private void IncreasePHashThreshold_Click(object sender, RoutedEventArgs e)
        {
            SetPerceptualSimilarityThreshold(perceptualSimilarityThreshold + 1);
        }

        private void SetByteSimilarityThreshold(int threshold)
        {
            int boundedThreshold = BoundSimilarityThreshold(threshold);
            if (boundedThreshold == byteSimilarityThreshold)
            {
                return;
            }

            byteSimilarityThreshold = boundedThreshold;
            ByteThresholdTextBlock.Text = byteSimilarityThreshold + "%";
            ThresholdsChanged();
        }

        private void SetPerceptualSimilarityThreshold(int threshold)
        {
            int boundedThreshold = BoundSimilarityThreshold(threshold);
            if (boundedThreshold == perceptualSimilarityThreshold)
            {
                return;
            }

            perceptualSimilarityThreshold = boundedThreshold;
            PHashThresholdTextBlock.Text = perceptualSimilarityThreshold + "%";
            ThresholdsChanged();
        }

        private static int BoundSimilarityThreshold(int threshold)
        {
            return Math.Max(
                MinimumPerceptualSimilarity,
                Math.Min(MaximumPerceptualSimilarity, threshold));
        }

        private void ThresholdsChanged()
        {
            UpdateThresholdControlState();
            ClearResults();
            StatusTextBlock.Text = "Similarity threshold changed. Start a new comparison to refresh the results.";
        }

        private void UpdateThresholdControlState()
        {
            DecreaseByteThresholdButton.IsEnabled =
                !isComparing && byteSimilarityThreshold > MinimumPerceptualSimilarity;
            IncreaseByteThresholdButton.IsEnabled =
                !isComparing && byteSimilarityThreshold < MaximumPerceptualSimilarity;
            DecreasePHashThresholdButton.IsEnabled =
                !isComparing && perceptualSimilarityThreshold > MinimumPerceptualSimilarity;
            IncreasePHashThresholdButton.IsEnabled =
                !isComparing && perceptualSimilarityThreshold < MaximumPerceptualSimilarity;
        }

        private static string ChooseFolder(string description, string initialPath)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(initialPath))
                {
                    dialog.SelectedPath = initialPath;
                }

                return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private async void Compare_Click(object sender, RoutedEventArgs e)
        {
            if (isComparing)
            {
                return;
            }

            string masterRoot = MasterFolderTextBox.Text;
            string currentRoot = CurrentFolderTextBox.Text;
            if (!Directory.Exists(masterRoot) || !Directory.Exists(currentRoot))
            {
                Logger.Warn("Comparison rejected: one or both selected folders do not exist.");
                MessageBox.Show(this, "Choose two existing folders before starting the comparison.",
                    "Folders required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (FoldersOverlap(masterRoot, currentRoot))
            {
                Logger.Warn("Comparison rejected because selected folders overlap. Master: "
                    + masterRoot + "; current: " + currentRoot);
                MessageBox.Show(this, "The selected folders must not be the same or contain one another. " +
                    "Choose separate master and current-images folders to protect your master files.",
                    "Folders overlap", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ClearResults();
            isComparing = true;
            isIndexing = false;
            isIndexingPaused = false;
            indexingPause.Set();
            CompareButton.IsEnabled = false;
            PauseIndexingButton.IsEnabled = false;
            SetPauseButtonIcon(false);
            ChooseMasterFolderButton.IsEnabled = false;
            ChooseCurrentFolderButton.IsEnabled = false;
            UpdateThresholdControlState();
            DeleteButton.IsEnabled = false;
            ProgressBar.Value = 0;
            StatusTextBlock.Text = "Preparing image scan...";
            IndexingEtaTextBlock.Visibility = Visibility.Collapsed;
            IndexingEtaTextBlock.Text = string.Empty;
            masterImagesAddedToCache = 0;
            currentImagesAddedToCache = 0;
            UpdateCacheStatus();
            Logger.Info("Comparison started. Master: " + masterRoot
                + "; current: " + currentRoot
                + "; byte threshold: " + byteSimilarityThreshold
                + "%; pHash threshold: " + perceptualSimilarityThreshold + "%.");

            var progress = new Progress<ScanProgress>(UpdateProgress);
            try
            {
                MatchScanResult foundMatches = await Task.Run(
                    () => FindMatches(
                        masterRoot,
                        currentRoot,
                        byteSimilarityThreshold,
                        perceptualSimilarityThreshold,
                        indexingPause,
                        progress));

                foreach (ImageMatch match in foundMatches)
                {
                    Matches.Add(match);
                }

                ResultsCountTextBlock.Text = foundMatches.Count + (foundMatches.Count == 1 ? " match" : " matches");
                string indexSummary = " Master cache: " + foundMatches.CachedMasterImages
                    + " hits, " + foundMatches.ProcessedMasterImages + " processed; current cache: "
                    + foundMatches.CachedCurrentImages + " hits, "
                    + foundMatches.ProcessedCurrentImages + " processed; "
                    + foundMatches.CacheFilesRead + " cache file(s) searched.";
                StatusTextBlock.Text = foundMatches.Count == 0
                    ? "Comparison complete. No images met both similarity thresholds." + indexSummary
                    : "Comparison complete. Exact files are selected; review similarity matches before selecting them." + indexSummary;
                UpdateCacheFileList(foundMatches.CacheFilePaths);
                Logger.Info("Comparison completed. Matches: " + foundMatches.Count
                    + "; master images read from cache: " + foundMatches.CachedMasterImages
                    + "; master images processed: " + foundMatches.ProcessedMasterImages
                    + "; current images read from cache: " + foundMatches.CachedCurrentImages
                    + "; current images processed: " + foundMatches.ProcessedCurrentImages + ".");
                ProgressBar.Value = 100;
                DeleteButton.IsEnabled = foundMatches.Count > 0;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = "Comparison failed.";
                Logger.Error("Comparison failed.", ex);
                MessageBox.Show(this, "The comparison could not be completed:\n\n" + ex.Message,
                    "Comparison error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isComparing = false;
                isIndexing = false;
                isIndexingPaused = false;
                indexingPause.Set();
                CompareButton.IsEnabled = true;
                PauseIndexingButton.IsEnabled = false;
                SetPauseButtonIcon(false);
                ChooseMasterFolderButton.IsEnabled = true;
                ChooseCurrentFolderButton.IsEnabled = true;
                UpdateThresholdControlState();
            }
        }

        private static MatchScanResult FindMatches(
            string masterRoot,
            string currentRoot,
            int byteThreshold,
            int perceptualThreshold,
            ManualResetEventSlim indexingPause,
            IProgress<ScanProgress> progress)
        {
            List<string> masterFiles = GetImageFiles(masterRoot);
            List<string> currentFiles = GetImageFiles(currentRoot);
            int total = masterFiles.Count + currentFiles.Count;
            int completed = 0;
            int cachedMasterImages = 0;
            string masterCachePath = Path.Combine(masterRoot, IndexCacheFileName);
            string currentCachePath = Path.Combine(currentRoot, IndexCacheFileName);
            List<string> masterCachePaths;
            List<string> currentCachePaths;
            Dictionary<string, IndexedImage> cachedMasterImagesByPath =
                ReadIndexCache(masterRoot, out masterCachePaths);
            Dictionary<string, IndexedImage> cachedCurrentImagesByPath =
                ReadIndexCache(currentRoot, out currentCachePaths);
            var cachePaths = masterCachePaths.Concat(currentCachePaths).ToList();
            int masterImagesToProcess = masterFiles.Count(
                path => !IsImageCached(path, cachedMasterImagesByPath));
            int processedMasterImages = 0;
            int cachedCurrentImages = 0;
            int processedCurrentImages = 0;
            int comparisonImagesCompleted = 0;
            TimeSpan comparisonImageProcessingTime = TimeSpan.Zero;
            TimeSpan imageProcessingTime = TimeSpan.Zero;
            Logger.Info("Scan discovered " + masterFiles.Count + " master and "
                + currentFiles.Count + " current images. Loaded "
                + cachedMasterImagesByPath.Count + " master and "
                + cachedCurrentImagesByPath.Count + " current cache records from "
                + cachePaths.Count + " cache file(s): " + string.Join("; ", cachePaths));
            progress.Report(new ScanProgress(
                completed,
                total,
                "Indexing master images",
                cachedMasterImages,
                processedMasterImages,
                cachedCurrentImages,
                processedCurrentImages,
                cachePaths,
                processedMasterImages,
                masterImagesToProcess,
                imageProcessingTime,
                comparisonImagesCompleted,
                currentFiles.Count,
                comparisonImageProcessingTime));

            var masterHashes = new Dictionary<string, IndexedImage>(StringComparer.Ordinal);
            var masterPerceptualHashes = new List<IndexedImage>();
            foreach (string path in masterFiles)
            {
                indexingPause.Wait();
                var fileInfo = new FileInfo(path);
                IndexedImage cachedImage;
                IndexedImage masterImage;
                if (cachedMasterImagesByPath.TryGetValue(Path.GetFullPath(path), out cachedImage)
                    && cachedImage.FileLength == fileInfo.Length
                    && cachedImage.LastWriteTimeUtcTicks == fileInfo.LastWriteTimeUtc.Ticks)
                {
                    masterImage = new IndexedImage(
                        path,
                        cachedImage.ContentHash,
                        cachedImage.PerceptualHash,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc.Ticks);
                    cachedMasterImages++;
                }
                else
                {
                    Logger.Info("Processing master image: " + path
                        + (cachedImage == null ? " (not cached)." : " (cache entry stale)."));
                    Stopwatch imageProcessingStopwatch = Stopwatch.StartNew();
                    string hash = GetFileHash(path);
                    ulong perceptualHash = GetPerceptualHash(path);
                    masterImage = new IndexedImage(
                        path,
                        hash,
                        perceptualHash,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc.Ticks);
                    AppendIndexCache(masterCachePath, masterImage);
                    imageProcessingStopwatch.Stop();
                    imageProcessingTime += imageProcessingStopwatch.Elapsed;
                    processedMasterImages++;
                }

                if (!masterHashes.ContainsKey(masterImage.ContentHash))
                {
                    masterHashes.Add(masterImage.ContentHash, masterImage);
                }

                masterPerceptualHashes.Add(masterImage);

                completed++;
                progress.Report(new ScanProgress(
                    completed,
                    total,
                    "Indexing master images",
                    cachedMasterImages,
                    processedMasterImages,
                    cachedCurrentImages,
                    processedCurrentImages,
                    cachePaths,
                    processedMasterImages,
                    masterImagesToProcess,
                    imageProcessingTime,
                    comparisonImagesCompleted,
                    currentFiles.Count,
                    comparisonImageProcessingTime));
            }

            Logger.Info("Master indexing finished. Cache hits: " + cachedMasterImages
                + "; newly processed: " + processedMasterImages + ".");
            var matches = new List<ImageMatch>();
            foreach (string currentPath in currentFiles)
            {
                Stopwatch comparisonStopwatch = Stopwatch.StartNew();
                var currentFileInfo = new FileInfo(currentPath);
                IndexedImage cachedCurrentImage;
                IndexedImage currentImage;
                if (cachedCurrentImagesByPath.TryGetValue(Path.GetFullPath(currentPath), out cachedCurrentImage)
                    && cachedCurrentImage.FileLength == currentFileInfo.Length
                    && cachedCurrentImage.LastWriteTimeUtcTicks == currentFileInfo.LastWriteTimeUtc.Ticks)
                {
                    currentImage = new IndexedImage(
                        currentPath,
                        cachedCurrentImage.ContentHash,
                        cachedCurrentImage.PerceptualHash,
                        currentFileInfo.Length,
                        currentFileInfo.LastWriteTimeUtc.Ticks);
                    cachedCurrentImages++;
                }
                else
                {
                    Logger.Info("Processing current image: " + currentPath
                        + (cachedCurrentImage == null ? " (not cached)." : " (cache entry stale)."));
                    string hash = GetFileHash(currentPath);
                    ulong perceptualHash = GetPerceptualHash(currentPath);
                    currentImage = new IndexedImage(
                        currentPath,
                        hash,
                        perceptualHash,
                        currentFileInfo.Length,
                        currentFileInfo.LastWriteTimeUtc.Ticks);
                    AppendIndexCache(currentCachePath, currentImage);
                    processedCurrentImages++;
                }

                IndexedImage exactMaster;
                if (masterHashes.TryGetValue(currentImage.ContentHash, out exactMaster)
                    && FilesAreIdentical(exactMaster.Path, currentPath))
                {
                    matches.Add(new ImageMatch(
                        exactMaster.Path,
                        currentPath,
                        true,
                        100,
                        100,
                        byteThreshold,
                        perceptualThreshold));
                }
                else
                {
                    IndexedImage bestMaster = null;
                    int bestPerceptualSimilarity = -1;
                    double bestByteSimilarity = -1;
                    long currentLength = currentFileInfo.Length;
                    foreach (IndexedImage candidate in masterPerceptualHashes)
                    {
                        int perceptualSimilarity =
                            GetPerceptualHashSimilarity(candidate.PerceptualHash, currentImage.PerceptualHash);
                        if (perceptualSimilarity < perceptualThreshold)
                        {
                            continue;
                        }

                        double byteSimilarity = GetByteSimilarity(
                            candidate.Path,
                            candidate.FileLength,
                            currentPath,
                            currentLength,
                            byteThreshold);
                        if (byteSimilarity >= byteThreshold
                            && (perceptualSimilarity > bestPerceptualSimilarity
                                || (perceptualSimilarity == bestPerceptualSimilarity
                                    && byteSimilarity > bestByteSimilarity)))
                        {
                            bestPerceptualSimilarity = perceptualSimilarity;
                            bestByteSimilarity = byteSimilarity;
                            bestMaster = candidate;
                        }
                    }

                    if (bestMaster != null)
                    {
                        matches.Add(new ImageMatch(
                            bestMaster.Path,
                            currentPath,
                            false,
                            bestByteSimilarity,
                            bestPerceptualSimilarity,
                            byteThreshold,
                            perceptualThreshold));
                    }
                }

                comparisonStopwatch.Stop();
                comparisonImagesCompleted++;
                comparisonImageProcessingTime += comparisonStopwatch.Elapsed;
                completed++;
                progress.Report(new ScanProgress(
                    completed,
                    total,
                    ComparingImagesStage,
                    cachedMasterImages,
                    processedMasterImages,
                    cachedCurrentImages,
                    processedCurrentImages,
                    cachePaths,
                    processedMasterImages,
                    masterImagesToProcess,
                    imageProcessingTime,
                    comparisonImagesCompleted,
                    currentFiles.Count,
                    comparisonImageProcessingTime));
            }

            return new MatchScanResult(
                matches,
                cachedMasterImages,
                processedMasterImages,
                cachedCurrentImages,
                processedCurrentImages,
                cachePaths);
        }

        private static Dictionary<string, IndexedImage> ReadIndexCache(
            string root,
            out List<string> cachePaths)
        {
            var cachedImages = new Dictionary<string, IndexedImage>(StringComparer.OrdinalIgnoreCase);
            cachePaths = Directory
                .EnumerateFiles(root, IndexCacheFileName, System.IO.SearchOption.AllDirectories)
                .ToList();
            foreach (string cachePath in cachePaths)
            {
                foreach (string line in File.ReadLines(cachePath))
                {
                    string[] fields = line.Split('|');
                    if (fields.Length != 5)
                    {
                        continue;
                    }

                    try
                    {
                        string path = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                        long fileLength = long.Parse(fields[1], CultureInfo.InvariantCulture);
                        long lastWriteTimeUtcTicks = long.Parse(fields[2], CultureInfo.InvariantCulture);
                        ulong perceptualHash = ulong.Parse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        if (fileLength < 0 || fields[3].Length != 64 || fields[3].Any(character => !Uri.IsHexDigit(character)))
                        {
                            continue;
                        }

                        cachedImages[Path.GetFullPath(path)] = new IndexedImage(
                            path,
                            fields[3],
                            perceptualHash,
                            fileLength,
                            lastWriteTimeUtcTicks);
                    }
                    catch (FormatException)
                    {
                        // Invalid cache records are ignored and recalculated during this scan.
                    }
                    catch (ArgumentException)
                    {
                        // Invalid cache paths are ignored and recalculated during this scan.
                    }
                    catch (OverflowException)
                    {
                        // Out-of-range cache values are ignored and recalculated during this scan.
                    }
                }
            }

            return cachedImages;
        }

        private static bool IsImageCached(string path, IDictionary<string, IndexedImage> cachedImages)
        {
            IndexedImage cachedImage;
            if (!cachedImages.TryGetValue(Path.GetFullPath(path), out cachedImage))
            {
                return false;
            }

            var fileInfo = new FileInfo(path);
            return cachedImage.FileLength == fileInfo.Length
                && cachedImage.LastWriteTimeUtcTicks == fileInfo.LastWriteTimeUtc.Ticks;
        }

        private static void AppendIndexCache(string cachePath, IndexedImage image)
        {
            string record = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(image.Path)))
                + "|" + image.FileLength.ToString(CultureInfo.InvariantCulture)
                + "|" + image.LastWriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture)
                + "|" + image.ContentHash
                + "|" + image.PerceptualHash.ToString("X16", CultureInfo.InvariantCulture)
                + Environment.NewLine;
            byte[] bytes = Encoding.UTF8.GetBytes(record);
            using (var stream = new FileStream(cachePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void RemoveIndexCacheEntry(string root, string imagePath)
        {
            string fullImagePath = Path.GetFullPath(imagePath);
            List<string> cachePaths = Directory
                .EnumerateFiles(root, IndexCacheFileName, System.IO.SearchOption.AllDirectories)
                .ToList();
            foreach (string cachePath in cachePaths)
            {
                List<string> lines = File.ReadAllLines(cachePath).ToList();
                List<string> retainedLines = lines
                    .Where(line => !IsCacheEntryForPath(line, fullImagePath))
                    .ToList();
                if (retainedLines.Count == lines.Count)
                {
                    continue;
                }

                if (retainedLines.Count == 0)
                {
                    File.Delete(cachePath);
                    continue;
                }

                string temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllLines(temporaryPath, retainedLines, new UTF8Encoding(false));
                    File.Replace(temporaryPath, cachePath, null);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }

        private static bool IsCacheEntryForPath(string line, string fullImagePath)
        {
            string[] fields = line.Split('|');
            if (fields.Length != 5)
            {
                return false;
            }

            try
            {
                string cachedPath = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                return string.Equals(
                    Path.GetFullPath(cachedPath),
                    fullImagePath,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static List<string> GetImageFiles(string root)
        {
            return Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                .ToList();
        }

        private static string GetFileHash(string path)
        {
            using (var sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        private static ulong GetPerceptualHash(string path)
        {
            try
            {
                return ComputePerceptualHash(path);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not calculate perceptual hash for image: " + path, ex);
                throw;
            }
        }

        private static ulong ComputePerceptualHash(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.DecodePixelWidth = PerceptualHashSize;
            image.DecodePixelHeight = PerceptualHashSize;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();

            var grayscale = new FormatConvertedBitmap(
                image,
                System.Windows.Media.PixelFormats.Gray8,
                null,
                0);
            byte[] pixels = new byte[PerceptualHashSize * PerceptualHashSize];
            grayscale.CopyPixels(pixels, PerceptualHashSize, 0);

            var coefficients = new double[LowFrequencySize, LowFrequencySize];
            var acCoefficients = new double[PerceptualHashBitCount - 1];
            int acIndex = 0;
            for (int verticalFrequency = 0; verticalFrequency < LowFrequencySize; verticalFrequency++)
            {
                for (int horizontalFrequency = 0; horizontalFrequency < LowFrequencySize; horizontalFrequency++)
                {
                    double coefficient = 0;
                    for (int y = 0; y < PerceptualHashSize; y++)
                    {
                        double verticalCosine = DctCosines[y, verticalFrequency];
                        int rowOffset = y * PerceptualHashSize;
                        for (int x = 0; x < PerceptualHashSize; x++)
                        {
                            coefficient += pixels[rowOffset + x]
                                * verticalCosine
                                * DctCosines[x, horizontalFrequency];
                        }
                    }

                    double verticalScale = verticalFrequency == 0
                        ? 1.0 / Math.Sqrt(PerceptualHashSize)
                        : Math.Sqrt(2.0 / PerceptualHashSize);
                    double horizontalScale = horizontalFrequency == 0
                        ? 1.0 / Math.Sqrt(PerceptualHashSize)
                        : Math.Sqrt(2.0 / PerceptualHashSize);
                    coefficient *= verticalScale * horizontalScale;
                    coefficients[verticalFrequency, horizontalFrequency] = coefficient;
                    if (verticalFrequency != 0 || horizontalFrequency != 0)
                    {
                        acCoefficients[acIndex++] = coefficient;
                    }
                }
            }

            Array.Sort(acCoefficients);
            double median = acCoefficients[acCoefficients.Length / 2];
            ulong hash = 0;
            int bitIndex = 0;
            for (int verticalFrequency = 0; verticalFrequency < LowFrequencySize; verticalFrequency++)
            {
                for (int horizontalFrequency = 0; horizontalFrequency < LowFrequencySize; horizontalFrequency++)
                {
                    if (coefficients[verticalFrequency, horizontalFrequency] > median)
                    {
                        hash |= 1UL << bitIndex;
                    }

                    bitIndex++;
                }
            }

            return hash;
        }

        private static double[,] CreateDctCosines()
        {
            var cosines = new double[PerceptualHashSize, LowFrequencySize];
            for (int pixel = 0; pixel < PerceptualHashSize; pixel++)
            {
                for (int frequency = 0; frequency < LowFrequencySize; frequency++)
                {
                    cosines[pixel, frequency] = Math.Cos(
                        ((2 * pixel + 1) * frequency * Math.PI) / (2 * PerceptualHashSize));
                }
            }

            return cosines;
        }

        private static int GetPerceptualHashSimilarity(ulong first, ulong second)
        {
            ulong difference = first ^ second;
            int differingBits = 0;
            while (difference != 0)
            {
                difference &= difference - 1;
                differingBits++;
            }

            return (int)Math.Round(
                (PerceptualHashBitCount - differingBits) * 100.0 / PerceptualHashBitCount);
        }

        private static double GetByteSimilarity(
            string firstPath,
            long firstLength,
            string secondPath,
            long secondLength,
            int threshold)
        {
            long denominator = Math.Max(firstLength, secondLength);
            if (denominator == 0)
            {
                return 100;
            }

            long comparableLength = Math.Min(firstLength, secondLength);
            long requiredMatches = (long)Math.Ceiling(denominator * threshold / 100.0);
            if (comparableLength < requiredMatches)
            {
                return -1;
            }

            long matchingBytes = 0;
            using (FileStream first = File.OpenRead(firstPath))
            using (FileStream second = File.OpenRead(secondPath))
            {
                byte[] firstBuffer = new byte[65536];
                byte[] secondBuffer = new byte[65536];
                long comparedBytes = 0;
                while (comparedBytes < comparableLength)
                {
                    int bytesToRead = (int)Math.Min(firstBuffer.Length, comparableLength - comparedBytes);
                    int firstRead = first.Read(firstBuffer, 0, bytesToRead);
                    int secondRead = second.Read(secondBuffer, 0, bytesToRead);
                    if (firstRead != bytesToRead || secondRead != bytesToRead)
                    {
                        throw new IOException("An image file changed while its byte similarity was being calculated.");
                    }

                    for (int i = 0; i < bytesToRead; i++)
                    {
                        if (firstBuffer[i] == secondBuffer[i])
                        {
                            matchingBytes++;
                        }
                    }

                    comparedBytes += bytesToRead;
                    if (matchingBytes + comparableLength - comparedBytes < requiredMatches)
                    {
                        return -1;
                    }
                }
            }

            return matchingBytes * 100.0 / denominator;
        }

        private static bool FilesAreIdentical(string firstPath, string secondPath)
        {
            if (new FileInfo(firstPath).Length != new FileInfo(secondPath).Length)
            {
                return false;
            }

            using (FileStream first = File.OpenRead(firstPath))
            using (FileStream second = File.OpenRead(secondPath))
            {
                byte[] firstBuffer = new byte[65536];
                byte[] secondBuffer = new byte[65536];
                while (true)
                {
                    int firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
                    int secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
                    if (firstRead != secondRead)
                    {
                        return false;
                    }

                    if (firstRead == 0)
                    {
                        return true;
                    }

                    for (int i = 0; i < firstRead; i++)
                    {
                        if (firstBuffer[i] != secondBuffer[i])
                        {
                            return false;
                        }
                    }
                }
            }
        }

        private static bool FoldersOverlap(string first, string second)
        {
            string firstPath = EnsureTrailingSeparator(Path.GetFullPath(first));
            string secondPath = EnsureTrailingSeparator(Path.GetFullPath(second));
            return firstPath.StartsWith(secondPath, StringComparison.OrdinalIgnoreCase)
                || secondPath.StartsWith(firstPath, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }

        private void UpdateProgress(ScanProgress progress)
        {
            isIndexing = progress.Stage == "Indexing master images";
            PauseIndexingButton.IsEnabled = isComparing && isIndexing;
            bool isEstimating = isIndexing || progress.Stage == ComparingImagesStage;
            if (isEstimating)
            {
                IndexingEtaTextBlock.Visibility = Visibility.Visible;
                UpdateEtaEstimate(
                    progress.Stage,
                    isIndexing ? progress.ProcessingImagesCompleted : progress.ComparisonImagesCompleted,
                    isIndexing ? progress.ProcessingImagesTotal : progress.ComparisonImagesTotal,
                    isIndexing ? progress.ImageProcessingTime : progress.ComparisonImageProcessingTime);
            }
            else
            {
                IndexingEtaTextBlock.Visibility = Visibility.Collapsed;
                IndexingEtaTextBlock.Text = string.Empty;
            }
            ProgressBar.Value = progress.Total == 0 ? 100 : progress.Completed * 100.0 / progress.Total;
            StatusTextBlock.Text = (isIndexingPaused ? "Indexing paused. " : string.Empty)
                + progress.Stage + ": " + progress.Completed + " of " + progress.Total
                + " images (master cache: " + progress.CachedMasterImages + " cached, "
                + progress.ProcessedMasterImages + " processed; current cache: "
                + progress.CachedCurrentImages + " cached, "
                + progress.ProcessedCurrentImages + " processed)";
            StatusTextBlock.Text += "; " + progress.CacheFilesRead + " cache file(s) searched";
            masterImagesAddedToCache = progress.ProcessedMasterImages;
            currentImagesAddedToCache = progress.ProcessedCurrentImages;
            UpdateCacheStatus();
            UpdateCacheFileList(progress.CacheFilePaths);
        }

        private void UpdateEtaEstimate(string stage, int processed, int toProcess, TimeSpan processingTime)
        {
            if (!string.Equals(etaEstimateStage, stage, StringComparison.Ordinal))
            {
                etaEstimateStage = stage;
                nextEtaAverageUpdate = EtaAverageUpdateInterval;
                etaAverageSecondsPerImage = null;
            }

            if (processed == 0)
            {
                etaAverageSecondsPerImage = null;
            }
            else if (processed >= nextEtaAverageUpdate || processed >= toProcess)
            {
                etaAverageSecondsPerImage = processingTime.TotalSeconds / processed;
                nextEtaAverageUpdate = ((processed / EtaAverageUpdateInterval) + 1)
                    * EtaAverageUpdateInterval;
            }

            string averageText = etaAverageSecondsPerImage.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "Avg: {0:0.00}s/image",
                    etaAverageSecondsPerImage.Value)
                : "Avg: calculating...";

            if (toProcess <= processed)
            {
                IndexingEtaTextBlock.Text = "ETA: 0s remaining | " + averageText;
                return;
            }

            if (!etaAverageSecondsPerImage.HasValue)
            {
                IndexingEtaTextBlock.Text = "ETA: calculating... | " + averageText;
                return;
            }

            double secondsRemaining = etaAverageSecondsPerImage.Value * (toProcess - processed);
            TimeSpan remaining = TimeSpan.FromSeconds(Math.Ceiling(secondsRemaining));
            string estimate;
            if (remaining.TotalHours >= 1)
            {
                estimate = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}h {1}m {2}s",
                    (int)remaining.TotalHours,
                    remaining.Minutes,
                    remaining.Seconds);
            }
            else if (remaining.TotalMinutes >= 1)
            {
                estimate = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}m {1}s",
                    remaining.Minutes,
                    remaining.Seconds);
            }
            else
            {
                estimate = remaining.Seconds + "s";
            }

            IndexingEtaTextBlock.Text = "ETA: ~" + estimate + " remaining | " + averageText;
        }

        private void UpdateCacheStatus()
        {
            string masterRoot = MasterFolderTextBox.Text;
            string currentRoot = CurrentFolderTextBox.Text;
            string masterCachePath = string.IsNullOrWhiteSpace(masterRoot)
                ? null
                : Path.Combine(masterRoot, IndexCacheFileName);
            string currentCachePath = string.IsNullOrWhiteSpace(currentRoot)
                ? null
                : Path.Combine(currentRoot, IndexCacheFileName);
            CacheStatusTextBlock.Text = "Added this run: master " + masterImagesAddedToCache
                + ", current " + currentImagesAddedToCache + ". Master cache: "
                + (masterCachePath ?? "choose a master folder")
                + "; current cache: " + (currentCachePath ?? "choose a current-images folder")
                + ". Each folder and its subfolders are searched.";
            CacheStatusTextBlock.ToolTip = "Master: " + (masterCachePath ?? "not selected")
                + Environment.NewLine + "Current: " + (currentCachePath ?? "not selected");
        }

        private void UpdateCacheFileList(IEnumerable<string> cachePaths)
        {
            List<string> paths = cachePaths.ToList();
            CacheFilesTextBlock.Text = paths.Count == 0
                ? "No existing cache files were found."
                : "Cache files searched:" + Environment.NewLine + string.Join(Environment.NewLine, paths);
            CacheFilesScrollViewer.Visibility = Visibility.Visible;
        }

        private void SetPauseButtonIcon(bool showResumeIcon)
        {
            PauseIndexingIcon.Data = System.Windows.Media.Geometry.Parse(showResumeIcon
                ? "M 2,1 L 10,6 L 2,11 Z"
                : "M 2,1 H 5 V 11 H 2 Z M 7,1 H 10 V 11 H 7 Z");
            PauseIndexingButton.ToolTip = showResumeIcon ? "Resume indexing" : "Pause indexing";
            System.Windows.Automation.AutomationProperties.SetName(
                PauseIndexingButton,
                showResumeIcon ? "Resume indexing" : "Pause indexing");
        }

        private void PauseIndexing_Click(object sender, RoutedEventArgs e)
        {
            if (!isComparing || !isIndexing)
            {
                return;
            }

            isIndexingPaused = !isIndexingPaused;
            if (isIndexingPaused)
            {
                indexingPause.Reset();
                SetPauseButtonIcon(true);
                StatusTextBlock.Text = "Indexing paused. " + StatusTextBlock.Text;
                Logger.Info("Master indexing paused.");
            }
            else
            {
                indexingPause.Set();
                SetPauseButtonIcon(false);
                StatusTextBlock.Text = StatusTextBlock.Text.Replace("Indexing paused. ", string.Empty);
                Logger.Info("Master indexing resumed.");
            }
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            List<ImageMatch> selected = Matches.Where(match => match.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select at least one current-folder copy to send to the Recycle Bin.",
                    "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(this,
                "Send " + selected.Count + (selected.Count == 1 ? " selected image" : " selected images") +
                " from the current-images folder to the Recycle Bin?\n\nMaster images will not be changed.",
                "Confirm recycle", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                Logger.Info("Recycle operation cancelled by user.");
                return;
            }

            string currentRoot = EnsureTrailingSeparator(Path.GetFullPath(CurrentFolderTextBox.Text));
            int recycled = 0;
            Logger.Info("Recycle operation started for " + selected.Count + " selected image(s).");
            var failures = new List<string>();
            var cacheCleanupFailures = new List<string>();

            foreach (ImageMatch match in selected)
            {
                string fullPath = Path.GetFullPath(match.CurrentPath);
                if (!fullPath.StartsWith(currentRoot, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(match.CurrentPath + ": file is outside the selected current-images folder.");
                    Logger.Warn("Recycle rejected for path outside current folder: " + fullPath);
                    continue;
                }

                try
                {
                    bool stillMatches;
                    if (match.IsExactFileMatch)
                    {
                        stillMatches = FilesAreIdentical(match.MasterPath, fullPath);
                    }
                    else
                    {
                        int perceptualSimilarity = GetPerceptualHashSimilarity(
                            GetPerceptualHash(match.MasterPath),
                            GetPerceptualHash(fullPath));
                        double byteSimilarity = GetByteSimilarity(
                            match.MasterPath,
                            new FileInfo(match.MasterPath).Length,
                            fullPath,
                            new FileInfo(fullPath).Length,
                            match.ByteSimilarityThreshold);
                        stillMatches = perceptualSimilarity >= match.PerceptualSimilarityThreshold
                            && byteSimilarity >= match.ByteSimilarityThreshold;
                    }
                    if (!stillMatches)
                    {
                        failures.Add(fullPath + ": file no longer matches its master image.");
                        continue;
                    }

                    FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    Matches.Remove(match);
                    recycled++;
                    Logger.Info("Sent current image to Recycle Bin: " + fullPath);
                    try
                    {
                        RemoveIndexCacheEntry(CurrentFolderTextBox.Text, fullPath);
                    }
                    catch (Exception ex)
                    {
                        cacheCleanupFailures.Add(fullPath + ": " + ex.Message);
                        Logger.Error("Could not remove the cache entry for recycled image " + fullPath + ".", ex);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(fullPath + ": " + ex.Message);
                    Logger.Error("Could not recycle " + fullPath + ".", ex);
                }
            }

            ResultsCountTextBlock.Text = Matches.Count + (Matches.Count == 1 ? " match" : " matches");
            DeleteButton.IsEnabled = Matches.Count > 0;
            StatusTextBlock.Text = recycled + (recycled == 1
                ? " selected copy sent to the Recycle Bin."
                : " selected copies sent to the Recycle Bin.")
                + (cacheCleanupFailures.Count == 0
                    ? string.Empty
                    : " Cache cleanup failed for " + cacheCleanupFailures.Count + " recycled image(s).");
            Logger.Info("Recycle operation finished. Recycled: " + recycled
                + "; recycle failures: " + failures.Count
                + "; cache cleanup failures: " + cacheCleanupFailures.Count + ".");

            if (failures.Count > 0 || cacheCleanupFailures.Count > 0)
            {
                var details = new List<string>();
                if (failures.Count > 0)
                {
                    details.Add("Could not recycle " + failures.Count + " image(s):");
                    details.AddRange(failures.Take(10));
                    if (failures.Count > 10)
                    {
                        details.Add("...and " + (failures.Count - 10) + " more.");
                    }
                }

                if (cacheCleanupFailures.Count > 0)
                {
                    details.Add("Recycled, but could not clean up cache entries for "
                        + cacheCleanupFailures.Count + " image(s):");
                    details.AddRange(cacheCleanupFailures.Take(10));
                    if (cacheCleanupFailures.Count > 10)
                    {
                        details.Add("...and " + (cacheCleanupFailures.Count - 10) + " more.");
                    }
                }

                MessageBox.Show(this,
                    recycled + " image(s) sent to the Recycle Bin.\n\n" + string.Join("\n", details),
                    cacheCleanupFailures.Count > 0 && failures.Count == 0
                        ? "Cache cleanup warning"
                        : "Recycle operation warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void ClearResults()
        {
            Matches.Clear();
            ResultsCountTextBlock.Text = string.Empty;
            ProgressBar.Value = 0;
            StatusTextBlock.Text = "Choose both folders to begin.";
            DeleteButton.IsEnabled = false;
        }

        private static void ConfigureLogging()
        {
            string logDirectory = Path.Combine(
                Path.GetTempPath(),
                "ImageDups");
            Directory.CreateDirectory(logDirectory);

            var hierarchy = (Hierarchy)LogManager.GetRepository();
            var appender = new RollingFileAppender
            {
                Name = "DailyFileAppender",
                File = Path.Combine(logDirectory, "ImageDups-"),
                AppendToFile = true,
                RollingStyle = RollingFileAppender.RollingMode.Date,
                DatePattern = "yyyyMMdd'.log'",
                StaticLogFileName = false,
                Layout = new PatternLayout("%date{yyyy-MM-dd HH:mm:ss.fff} [%level] %message%newline")
            };
            appender.ActivateOptions();
            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = log4net.Core.Level.Info;
            hierarchy.Configured = true;
        }
    }

    internal sealed class IndexedImage
    {
        public string Path { get; private set; }
        public string ContentHash { get; private set; }
        public ulong PerceptualHash { get; private set; }
        public long FileLength { get; private set; }
        public long LastWriteTimeUtcTicks { get; private set; }

        public IndexedImage(
            string path,
            string contentHash,
            ulong perceptualHash,
            long fileLength,
            long lastWriteTimeUtcTicks)
        {
            Path = path;
            ContentHash = contentHash;
            PerceptualHash = perceptualHash;
            FileLength = fileLength;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        }
    }

    internal sealed class MatchScanResult : List<ImageMatch>
    {
        public int CachedMasterImages { get; private set; }
        public int ProcessedMasterImages { get; private set; }
        public int CachedCurrentImages { get; private set; }
        public int ProcessedCurrentImages { get; private set; }
        public List<string> CacheFilePaths { get; private set; }
        public int CacheFilesRead { get { return CacheFilePaths.Count; } }
        public MatchScanResult(
            IEnumerable<ImageMatch> matches,
            int cachedMasterImages,
            int processedMasterImages,
            int cachedCurrentImages,
            int processedCurrentImages,
            IEnumerable<string> cacheFilePaths)
            : base(matches)
        {
            CachedMasterImages = cachedMasterImages;
            ProcessedMasterImages = processedMasterImages;
            CachedCurrentImages = cachedCurrentImages;
            ProcessedCurrentImages = processedCurrentImages;
            CacheFilePaths = cacheFilePaths.ToList();
        }
    }

    public sealed class ImageMatch
    {
        public string MasterPath { get; private set; }
        public string CurrentPath { get; private set; }
        public BitmapImage MasterThumbnail { get; private set; }
        public BitmapImage CurrentThumbnail { get; private set; }
        public string MasterDetails { get; private set; }
        public string CurrentDetails { get; private set; }
        public string ComparisonDetails { get; private set; }
        public bool IsExactFileMatch { get; private set; }
        public int ByteSimilarityThreshold { get; private set; }
        public int PerceptualSimilarityThreshold { get; private set; }
        public bool IsSelected { get; set; }

        public ImageMatch(
            string masterPath,
            string currentPath,
            bool isExactFileMatch,
            double byteSimilarity,
            int perceptualHashSimilarity,
            int byteSimilarityThreshold,
            int perceptualSimilarityThreshold)
        {
            MasterPath = masterPath;
            CurrentPath = currentPath;
            MasterThumbnail = LoadThumbnail(masterPath);
            CurrentThumbnail = LoadThumbnail(currentPath);
            MasterDetails = LoadDetails(masterPath);
            CurrentDetails = LoadDetails(currentPath);
            IsExactFileMatch = isExactFileMatch;
            ByteSimilarityThreshold = byteSimilarityThreshold;
            PerceptualSimilarityThreshold = perceptualSimilarityThreshold;
            ComparisonDetails = string.Format(
                "SHA-256 exact-file indicator: {0}% ({1})\nByte-content similarity: {2:0.##}% (threshold: {3}%)\npHash similarity: {4}% (threshold: {5}%; {6})",
                isExactFileMatch ? 100 : 0,
                isExactFileMatch ? "identical file" : "different file content",
                byteSimilarity,
                byteSimilarityThreshold,
                perceptualHashSimilarity,
                perceptualSimilarityThreshold,
                isExactFileMatch ? "exact-file match" : "pHash match; review before recycle");
            IsSelected = isExactFileMatch;
        }

        private static string LoadDetails(string path)
        {
            var file = new FileInfo(path);
            BitmapDecoder decoder = BitmapDecoder.Create(
                new Uri(path),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            string format = decoder.CodecInfo == null
                ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
                : decoder.CodecInfo.FriendlyName;

            return string.Format(
                "Dimensions: {0} × {1}\nSize: {2}\nFormat: {3}\nModified: {4}",
                frame.PixelWidth,
                frame.PixelHeight,
                FormatFileSize(file.Length),
                format,
                file.LastWriteTime.ToString("g"));
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? string.Format("{0} {1}", bytes, units[unitIndex])
                : string.Format("{0:0.##} {1}", size, units[unitIndex]);
        }

        private static BitmapImage LoadThumbnail(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 240;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }

    internal sealed class ScanProgress
    {
        public int Completed { get; private set; }
        public int Total { get; private set; }
        public string Stage { get; private set; }
        public int CachedMasterImages { get; private set; }
        public int ProcessedMasterImages { get; private set; }
        public int CachedCurrentImages { get; private set; }
        public int ProcessedCurrentImages { get; private set; }
        public List<string> CacheFilePaths { get; private set; }
        public int CacheFilesRead { get { return CacheFilePaths.Count; } }
        public int ProcessingImagesCompleted { get; private set; }
        public int ProcessingImagesTotal { get; private set; }
        public TimeSpan ImageProcessingTime { get; private set; }
        public int ComparisonImagesCompleted { get; private set; }
        public int ComparisonImagesTotal { get; private set; }
        public TimeSpan ComparisonImageProcessingTime { get; private set; }

        public ScanProgress(
            int completed,
            int total,
            string stage,
            int cachedMasterImages,
            int processedMasterImages,
            int cachedCurrentImages,
            int processedCurrentImages,
            IEnumerable<string> cacheFilePaths,
            int processingImagesCompleted,
            int processingImagesTotal,
            TimeSpan imageProcessingTime,
            int comparisonImagesCompleted,
            int comparisonImagesTotal,
            TimeSpan comparisonImageProcessingTime)
        {
            Completed = completed;
            Total = total;
            Stage = stage;
            CachedMasterImages = cachedMasterImages;
            ProcessedMasterImages = processedMasterImages;
            CachedCurrentImages = cachedCurrentImages;
            ProcessedCurrentImages = processedCurrentImages;
            CacheFilePaths = cacheFilePaths.ToList();
            ProcessingImagesCompleted = processingImagesCompleted;
            ProcessingImagesTotal = processingImagesTotal;
            ImageProcessingTime = imageProcessingTime;
            ComparisonImagesCompleted = comparisonImagesCompleted;
            ComparisonImagesTotal = comparisonImagesTotal;
            ComparisonImageProcessingTime = comparisonImageProcessingTime;
        }
    }
}
