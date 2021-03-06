﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using HandBrake.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using HandBrake.Interop.Model;
using HandBrake.Interop.Model.Encoding;
using VidCoder.Messages;
using System.IO;
using VidCoder.Model.Encoding;
using VidCoder.Services;
using System.Threading;
using VidCoder.Model;
using System.Diagnostics;
using VidCoder.ViewModel.Components;

namespace VidCoder.ViewModel
{
	using System.Drawing;
	using System.Globalization;
	using System.Linq.Expressions;
	using Resources;

	public class PreviewViewModel : OkCancelDialogViewModel
	{
		private const int PreviewImageCacheDistance = 1;

		private static readonly TimeSpan MinPreviewImageRefreshInterval = TimeSpan.FromSeconds(0.5);
		private static int updateVersion;

		private VCJob job;
		private HandBrakeInstance originalScanInstance;
		private IEncodeProxy encodeProxy;
		private ILogger logger = Ioc.Container.GetInstance<ILogger>();
		private string title;
		private int selectedPreview;
		private bool hasPreview;
		private bool previewEncodeStarted;
		private bool generatingPreview;
		private string previewFilePath;
		private bool cancelPending;
		private bool encodeCancelled;
		private double previewPercentComplete;
		private int previewSeconds;
		private int previewCount;

		private DateTime lastImageRefreshTime;
		private System.Timers.Timer previewImageRefreshTimer;
		private bool waitingOnRefresh;
		private BitmapSource[] previewImageCache;
		private Queue<PreviewImageJob> previewImageWorkQueue = new Queue<PreviewImageJob>();
		private bool previewImageQueueProcessing;
		private object imageSync = new object();
		private List<object> imageFileSync;
		private string imageFileCacheFolder;
		private BitmapSource previewBitmapSource;

		private MainViewModel mainViewModel = Ioc.Container.GetInstance<MainViewModel>();
		private OutputPathViewModel outputPathVM = Ioc.Container.GetInstance<OutputPathViewModel>();
		private WindowManagerViewModel windowManagerVM = Ioc.Container.GetInstance<WindowManagerViewModel>();
		private ProcessingViewModel processingVM = Ioc.Container.GetInstance<ProcessingViewModel>();

		public PreviewViewModel()
		{
			Messenger.Default.Register<RefreshPreviewMessage>(
				this,
				message =>
				{
					this.RequestRefreshPreviews();
				});

			this.previewSeconds = Config.PreviewSeconds;
			this.displayType = CustomConfig.PreviewDisplay;
			this.selectedPreview = 1;
			this.Title = PreviewRes.NoVideoSourceTitle;

			this.RequestRefreshPreviews();
		}

		public MainViewModel MainViewModel
		{
			get
			{
				return this.mainViewModel;
			}
		}

		public WindowManagerViewModel WindowManagerVM
		{
			get
			{
				return this.windowManagerVM;
			}
		}

		public ProcessingViewModel ProcessingVM
		{
			get
			{
				return this.processingVM;
			}
		}

		public OutputPathViewModel OutputPathVM
		{
			get
			{
				return this.outputPathVM;
			}
		}

		public string Title
		{
			get
			{
				return this.title;
			}

			set
			{
				this.title = value;
				this.RaisePropertyChanged(() => this.Title);
			}
		}

		private ImageSource previewImage;
		public ImageSource PreviewImage
		{
			get
			{
				return this.previewImage;
			}

			set
			{
				this.previewImage = value;
				this.RaisePropertyChanged(() => this.PreviewImage);
			}
		}

		public BitmapSource PreviewBitmapSource
		{
			get
			{
				lock (this.imageSync)
				{
					return this.previewBitmapSource;
				}
			}
		}

		public bool GeneratingPreview
		{
			get
			{
				return this.generatingPreview;
			}

			set
			{
				this.generatingPreview = value;
				this.RaisePropertyChanged(() => this.GeneratingPreview);
				this.RaisePropertyChanged(() => this.SeekBarEnabled);
			}
		}

		public bool InCornerDisplayMode
		{
			get
			{
				return this.DisplayType == PreviewDisplay.Corners;
			}
		}

		public bool SeekBarEnabled
		{
			get
			{
				return this.HasPreview && !this.GeneratingPreview;
			}
		}

		public double PreviewPercentComplete
		{
			get
			{
				return this.previewPercentComplete;
			}

			set
			{
				this.previewPercentComplete = value;
				this.RaisePropertyChanged(() => this.PreviewPercentComplete);
			}
		}

		public int PreviewSeconds
		{
			get
			{
				return this.previewSeconds;
			}

			set
			{
				this.previewSeconds = value;
				this.RaisePropertyChanged(() => this.PreviewSeconds);

				Config.PreviewSeconds = value;
			}
		}

		public bool HasPreview
		{
			get
			{
				return this.hasPreview;
			}

			set
			{
				this.hasPreview = value;
				this.GeneratePreviewCommand.RaiseCanExecuteChanged();
				this.PlaySourceCommand.RaiseCanExecuteChanged();
				this.RaisePropertyChanged(() => this.PlaySourceToolTip);
				this.RaisePropertyChanged(() => this.SeekBarEnabled);
				this.RaisePropertyChanged(() => this.HasPreview);
				this.RaisePropertyChanged(() => this.PlayAvailable);
				this.RaisePropertyChanged(() => this.SingleOneToOneImageVisible);
				this.RaisePropertyChanged(() => this.SingleFitImageVisible);
				this.RaisePropertyChanged(() => this.CornersImagesVisible);
			}
		}

		public int SelectedPreview
		{
			get
			{
				return this.selectedPreview;
			}

			set
			{
				this.selectedPreview = value;
				this.RaisePropertyChanged(() => this.SelectedPreview);

				if (this.DisplayType == PreviewDisplay.Corners)
				{
					this.RequestRefreshPreviews();
				}
				else
				{
					lock (this.imageSync)
					{
						this.previewBitmapSource = this.previewImageCache[value];
						this.RefreshFromBitmapImage();
						this.ClearOutOfRangeItems();
						this.BeginBackgroundImageLoad();
					}
				}
			}
		}

		public bool SingleFitImageVisible
		{
			get
			{
				return this.HasPreview && this.DisplayType == PreviewDisplay.FitToWindow;
			}
		}

		public bool SingleOneToOneImageVisible
		{
			get
			{
				return this.HasPreview && this.DisplayType == PreviewDisplay.OneToOne;
			}
		}

		public bool CornersImagesVisible
		{
			get
			{
				return this.HasPreview && this.DisplayType == PreviewDisplay.Corners;
			}
		}

		/// <summary>
		/// Gets or sets the display width of the preview image in pixels.
		/// </summary>
		public double PreviewDisplayWidth { get; set; }

		/// <summary>
		/// Gets or sets the display height of the preview image in pixels.
		/// </summary>
		public double PreviewDisplayHeight { get; set; }

		public int SliderMax
		{
			get
			{
				if (this.previewCount > 0)
				{
					return this.previewCount - 1;
				}

				return Config.PreviewCount - 1;
			}
		}

		public int PreviewCount
		{
			get
			{
				if (this.previewCount > 0)
				{
					return this.previewCount;
				}

				return Config.PreviewCount;
			}
		}

		public string PlaySourceToolTip
		{
			get
			{
				if (!this.HasPreview || this.mainViewModel.SourcePath == null)
				{
					return null;
				}

				string sourcePath = this.mainViewModel.SourcePath;

				try
				{
					if (Utilities.IsDirectory(sourcePath))
					{
						// Path is a directory. Can only preview when it's a DVD and we have a supported player installed.
						bool isDvd = Utilities.IsDvdFolder(this.mainViewModel.SourcePath);
						if (!isDvd)
						{
							return PreviewRes.PlaySourceDisabledBluRayToolTip;
						}

						bool playerInstalled = Players.Installed.Count > 0;
						if (!playerInstalled)
						{
							return PreviewRes.PlaySourceDisabledNoPlayerToolTip;
						}
					}
				}
				catch (FileNotFoundException)
				{
					return PreviewRes.PlaySourceDisabledNotFoundToolTip;
				}
				catch (IOException)
				{
				}

				return PreviewRes.PlaySourceToolTip;
			}
		}

		public bool PlayAvailable
		{
			get
			{
				string sourcePath = this.mainViewModel.SourcePath;

				if (!this.HasPreview || sourcePath == null)
				{
					return false;
				}

				try
				{
					if (Utilities.IsDirectory(sourcePath))
					{
						// Path is a directory. Can only preview when it's a DVD and we have a supported player installed.
						bool isDvd = Utilities.IsDvdFolder(this.mainViewModel.SourcePath);
						bool playerInstalled = Players.Installed.Count > 0;

						return isDvd && playerInstalled;
					}
					else
					{
						// Path is a file
						return true;
					}
				}
				catch (IOException)
				{
					this.RaisePropertyChanged(() => this.PlaySourceToolTip);
					return false;
				}
			}
		}

		private PreviewDisplay displayType;
		public PreviewDisplay DisplayType
		{
			get
			{
				return this.displayType;
			}

			set
			{
				if (this.displayType != value)
				{
					this.displayType = value;
					this.RaisePropertyChanged(() => this.DisplayType);
					this.RaisePropertyChanged(() => this.SingleFitImageVisible);
					this.RaisePropertyChanged(() => this.SingleOneToOneImageVisible);
					this.RaisePropertyChanged(() => this.CornersImagesVisible);
					this.RaisePropertyChanged(() => this.InCornerDisplayMode);

					CustomConfig.PreviewDisplay = value;

					this.RequestRefreshPreviews();
				}
			}
		}

		public HandBrakeInstance ScanInstance
		{
			get
			{
				return this.mainViewModel.ScanInstance;
			}
		}

		private RelayCommand generatePreviewCommand;
		public RelayCommand GeneratePreviewCommand
		{
			get
			{
				return this.generatePreviewCommand ?? (this.generatePreviewCommand = new RelayCommand(() =>
					{
						this.job = this.mainViewModel.EncodeJob;

						this.PreviewPercentComplete = 0;
						this.GeneratingPreview = true;
						this.cancelPending = false;
						this.encodeCancelled = false;

						this.SetPreviewFilePath();

						this.job.OutputPath = this.previewFilePath;

						this.previewEncodeStarted = false;

						this.encodeProxy = Utilities.CreateEncodeProxy();
						this.encodeProxy.EncodeStarted += (o, e) =>
						{
							this.previewEncodeStarted = true;
							if (this.cancelPending)
							{
								DispatchService.BeginInvoke(() =>
								    {
										this.CancelPreviewEncode();
								    });
							}
							DispatchService.BeginInvoke(() =>
							{
								this.CancelPreviewCommand.RaiseCanExecuteChanged();
							});
						};
						this.encodeProxy.EncodeProgress += (o, e) =>
						{
							this.PreviewPercentComplete = e.FractionComplete * 100;
						};
						this.encodeProxy.EncodeCompleted += (o, e) =>
						{
							this.GeneratingPreview = false;

							if (this.encodeCancelled)
							{
								this.logger.Log("Cancelled preview clip generation");
							}
							else
							{
								if (e.Error)
								{
									this.logger.Log(PreviewRes.PreviewClipGenerationFailedTitle);
									Utilities.MessageBox.Show(PreviewRes.PreviewClipGenerationFailedMessage);
								}
								else
								{
									var previewFileInfo = new FileInfo(this.previewFilePath);
									this.logger.Log("Finished preview clip generation. Size: " + Utilities.FormatFileSize(previewFileInfo.Length));

									FileService.Instance.PlayVideo(previewFilePath);
								}
							}
						};

						this.logger.Log("Generating preview clip");
						this.logger.Log("  Path: " + this.job.OutputPath);
						this.logger.Log("  Title: " + this.job.Title);
						this.logger.Log("  Preview #: " + this.SelectedPreview);

						this.encodeProxy.StartEncode(this.job, this.logger, true, this.SelectedPreview, this.PreviewSeconds, this.job.Length.TotalSeconds);
						this.CancelPreviewCommand.RaiseCanExecuteChanged();
					}, () =>
					{
						return this.HasPreview;
					}));
			}
		}

		private void SetPreviewFilePath()
		{
			string extension = OutputPathViewModel.GetExtensionForProfile(this.job.EncodingProfile);

			string previewDirectory;
			if (Config.UseCustomPreviewFolder)
			{
				previewDirectory = Config.PreviewOutputFolder;
			}
			else
			{
				previewDirectory = Utilities.LocalAppFolder;
			}

			try
			{
				if (!Directory.Exists(previewDirectory))
				{
					Directory.CreateDirectory(previewDirectory);
				}
			}
			catch (Exception exception)
			{
				this.logger.LogError("Could not create preview directory " + Config.PreviewOutputFolder + Environment.NewLine + exception);
				previewDirectory = Utilities.LocalAppFolder;
			}

			this.previewFilePath = Path.Combine(previewDirectory, "preview" + extension);

			if (File.Exists(this.previewFilePath))
			{
				try
				{
					File.Delete(this.previewFilePath);
				}
				catch (Exception)
				{
					this.previewFilePath = Utilities.CreateUniqueFileName(this.previewFilePath, new HashSet<string>());
				}
			}
		}

		private RelayCommand playSourceCommand;
		public RelayCommand PlaySourceCommand
		{
			get
			{
				return this.playSourceCommand ?? (this.playSourceCommand = new RelayCommand(() =>
				    {
						string sourcePath = this.mainViewModel.SourcePath;

					    try
					    {
						    if (Utilities.IsDirectory(sourcePath))
						    {
								// Path is a directory
								IVideoPlayer player = Players.Installed.FirstOrDefault(p => p.Id == Config.PreferredPlayer);
								if (player == null)
								{
									player = Players.Installed[0];
								}

								player.PlayTitle(sourcePath, this.mainViewModel.SelectedTitle.TitleNumber);
						    }
						    else
						    {
								// Path is a file
								FileService.Instance.PlayVideo(sourcePath);
						    }
					    }
					    catch (IOException)
					    {
							this.PlaySourceCommand.RaiseCanExecuteChanged();
					    }
				    }, () =>
				    {
						return this.PlayAvailable;
				    }));
			}
		}

		private RelayCommand cancelPreviewCommand;
		public RelayCommand CancelPreviewCommand
		{
			get
			{
				return this.cancelPreviewCommand ?? (this.cancelPreviewCommand = new RelayCommand(() =>
					{
						this.CancelPreviewEncode();
					},
					() =>
					{
						return this.GeneratingPreview && this.previewEncodeStarted;
					}));
			}
		}

		public void RequestRefreshPreviews()
		{
			if (!this.mainViewModel.HasVideoSource)
			{
				this.HasPreview = false;
				this.Title = PreviewRes.NoVideoSourceTitle;
				this.TryCancelPreviewEncode();
				return;
			}

			if (this.originalScanInstance != this.ScanInstance || (this.job != null && this.job.Title != this.mainViewModel.EncodeJob.Title))
			{
				this.TryCancelPreviewEncode();
			}

			if (this.waitingOnRefresh)
			{
				return;
			}

			DateTime now = DateTime.Now;
			TimeSpan timeSinceLastRefresh = now - this.lastImageRefreshTime;
			if (timeSinceLastRefresh < MinPreviewImageRefreshInterval)
			{
				this.waitingOnRefresh = true;
				TimeSpan timeUntilNextRefresh = MinPreviewImageRefreshInterval - timeSinceLastRefresh;
				this.previewImageRefreshTimer = new System.Timers.Timer(timeUntilNextRefresh.TotalMilliseconds);
				this.previewImageRefreshTimer.Elapsed += this.previewImageRefreshTimer_Elapsed;
				this.previewImageRefreshTimer.AutoReset = false;
				this.previewImageRefreshTimer.Start();

				return;
			}

			this.lastImageRefreshTime = now;

			this.RefreshPreviews();
		}

		public void StopAndWait()
		{
			this.encodeCancelled = true;
			this.encodeProxy.StopAndWait();
		}

		private void previewImageRefreshTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			this.waitingOnRefresh = false;
			this.lastImageRefreshTime = DateTime.MinValue;
			DispatchService.BeginInvoke(this.RefreshPreviews);
		}

		private void RefreshPreviews()
		{
			this.originalScanInstance = this.ScanInstance;

			this.job = this.mainViewModel.EncodeJob;
			VCProfile profile = this.job.EncodingProfile;

			int width, height, parWidth, parHeight;
			this.ScanInstance.GetSize(this.job.HbJob, out width, out height, out parWidth, out parHeight);

			// If we're rotating by 90 degrees, swap width and height for sizing purposes.
            if (profile.Rotation == VCPictureRotation.Clockwise90 || profile.Rotation == VCPictureRotation.Clockwise270)
			{
				int temp = width;
				width = height;
				height = temp;

				temp = parWidth;
				parWidth = parHeight;
				parHeight = temp;
			}

			if (parWidth <= 0 || parHeight <= 0)
			{
				this.HasPreview = false;
				this.Title = PreviewRes.NoVideoSourceTitle;

				Ioc.Container.GetInstance<ILogger>().LogError("HandBrake returned a negative pixel aspect ratio. Cannot show preview.");
				return;
			}

			this.PreviewDisplayHeight = height;
			this.PreviewDisplayWidth = width * ((double)parWidth / parHeight);

			// Update the number of previews.
			this.previewCount = this.ScanInstance.PreviewCount;
			if (this.selectedPreview >= this.previewCount)
			{
				this.selectedPreview = this.previewCount - 1;
				this.RaisePropertyChanged(() => this.SelectedPreview);
			}

			this.RaisePropertyChanged(() => this.PreviewCount);

			this.HasPreview = true;

			lock (this.imageSync)
			{
				this.previewImageCache = new BitmapSource[this.previewCount];
				updateVersion++;

				// Clear main work queue.
				this.previewImageWorkQueue.Clear();

				this.imageFileCacheFolder = Path.Combine(Utilities.ImageCacheFolder,
														 Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
														 updateVersion.ToString(CultureInfo.InvariantCulture));
				if (!Directory.Exists(this.imageFileCacheFolder))
				{
					Directory.CreateDirectory(this.imageFileCacheFolder);
				}

				// Clear old images out of the file cache.
				this.ClearImageFileCache();

				this.imageFileSync = new List<object>(this.previewCount);
				for (int i = 0; i < this.previewCount; i++)
				{
					this.imageFileSync.Add(new object());
				}

				this.BeginBackgroundImageLoad();
			}

			if (parWidth == parHeight)
			{
				this.Title = string.Format(PreviewRes.PreviewWindowTitleSimple, width, height);
			}
			else
			{
				this.Title = string.Format(
					PreviewRes.PreviewWindowTitleComplex,
					Math.Round(this.PreviewDisplayWidth),
					Math.Round(this.PreviewDisplayHeight),
					width,
					height);
			}
		}

		private void ClearImageFileCache()
		{
			try
			{
				string processCacheFolder = Path.Combine(Utilities.ImageCacheFolder, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
				if (!Directory.Exists(processCacheFolder))
				{
					return;
				}

				int lowestUpdate = -1;
				for (int i = updateVersion - 1; i >= 1; i--)
				{
					if (Directory.Exists(Path.Combine(processCacheFolder, i.ToString(CultureInfo.InvariantCulture))))
					{
						lowestUpdate = i;
					}
					else
					{
						break;
					}
				}

				if (lowestUpdate == -1)
				{
					return;
				}

				for (int i = lowestUpdate; i <= updateVersion - 1; i++)
				{
					Utilities.DeleteDirectory(Path.Combine(processCacheFolder, i.ToString(CultureInfo.InvariantCulture)));
				}
			}
			catch (IOException)
			{
				// Ignore. Later checks will clear the cache.
			}
		}

		private void TryCancelPreviewEncode()
		{
			if (this.GeneratingPreview)
			{
				if (this.previewEncodeStarted)
				{
					this.CancelPreviewEncode();
				}
				else
				{
					this.cancelPending = true;
				}
			}
		}

		private void CancelPreviewEncode()
		{
			this.encodeCancelled = true;
			this.encodeProxy.StopEncode();
		}

		private void ClearOutOfRangeItems()
		{
			// Remove out of range items from work queue
			var newWorkQueue = new Queue<PreviewImageJob>();
			while (this.previewImageWorkQueue.Count > 0)
			{
				PreviewImageJob job = this.previewImageWorkQueue.Dequeue();
				if (Math.Abs(job.PreviewNumber - this.SelectedPreview) <= PreviewImageCacheDistance)
				{
					newWorkQueue.Enqueue(job);
				}
			}

			// Remove out of range cache entries
			for (int i = 0; i < this.previewCount; i++)
			{
				if (Math.Abs(i - this.SelectedPreview) > PreviewImageCacheDistance)
				{
					this.previewImageCache[i] = null;
				}
			}
		}

		private void BeginBackgroundImageLoad()
		{
			int currentPreview = this.SelectedPreview;

			if (!ImageLoadedOrLoading(currentPreview))
			{
				this.EnqueueWork(currentPreview);
			}

			for (int i = 1; i <= PreviewImageCacheDistance; i++)
			{
				if (currentPreview - i >= 0 && !ImageLoadedOrLoading(currentPreview - i))
				{
					EnqueueWork(currentPreview - i);
				}

				if (currentPreview + i < this.previewCount && !ImageLoadedOrLoading(currentPreview + i))
				{
					EnqueueWork(currentPreview + i);
				}
			}

			// Start a queue processing thread if one is not going already.
			if (!this.previewImageQueueProcessing && this.previewImageWorkQueue.Count > 0)
			{
				ThreadPool.QueueUserWorkItem(this.ProcessPreviewImageWork);
				this.previewImageQueueProcessing = true;
			}
		}

		private bool ImageLoadedOrLoading(int previewNumber)
		{
			if (this.previewImageCache[previewNumber] != null)
			{
				return true;
			}

			if (this.previewImageWorkQueue.Count(j => j.PreviewNumber == previewNumber) > 0)
			{
				return true;
			}

			return false;
		}

		private void EnqueueWork(int previewNumber)
		{
			this.previewImageWorkQueue.Enqueue(
				new PreviewImageJob
				{
					UpdateVersion = updateVersion,
					ScanInstance = this.ScanInstance,
					PreviewNumber = previewNumber,
					EncodeJob = this.job,
					ImageFileSync = this.imageFileSync[previewNumber]
				});
		}

		private void ProcessPreviewImageWork(object state)
		{
			PreviewImageJob imageJob;
			bool workLeft = true;

			while (workLeft)
			{
				lock (this.imageSync)
				{
					if (this.previewImageWorkQueue.Count == 0)
					{
						this.previewImageQueueProcessing = false;
						return;
					}

					imageJob = this.previewImageWorkQueue.Dequeue();
				}

				string imagePath = Path.Combine(
					Utilities.ImageCacheFolder,
					Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
					imageJob.UpdateVersion.ToString(CultureInfo.InvariantCulture),
					imageJob.PreviewNumber + ".bmp");
				BitmapSource imageSource = null;

				// Check the disc cache for the image
				lock (imageJob.ImageFileSync)
				{
					if (File.Exists(imagePath))
					{
						// When we read from disc cache the image is already transformed.
						var bitmapImage = new BitmapImage();
						bitmapImage.BeginInit();
						bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
						bitmapImage.UriSource = new Uri(imagePath);
						bitmapImage.EndInit();
						bitmapImage.Freeze();

						imageSource = bitmapImage;
					}
				}

				if (imageSource == null)
				{
					// Make a HandBrake call to get the image
					imageSource = imageJob.ScanInstance.GetPreview(imageJob.EncodeJob.HbJob, imageJob.PreviewNumber);

					// Transform the image as per rotation and reflection settings
					VCProfile profile = imageJob.EncodeJob.EncodingProfile;
                    if (profile.FlipHorizontal || profile.FlipVertical || profile.Rotation != VCPictureRotation.None)
					{
						imageSource = CreateTransformedBitmap(imageSource, profile);
					}

					// Start saving the image file in the background and continue to process the queue.
					ThreadPool.QueueUserWorkItem(
						this.BackgroundFileSave,
						new SaveImageJob
						{
							PreviewNumber = imageJob.PreviewNumber,
							UpdateVersion = imageJob.UpdateVersion,
							FilePath = imagePath,
							Image = imageSource,
							ImageFileSync = imageJob.ImageFileSync
						});
				}

				lock (this.imageSync)
				{
					if (imageJob.UpdateVersion == updateVersion)
					{
						this.previewImageCache[imageJob.PreviewNumber] = imageSource;
						if (this.SelectedPreview == imageJob.PreviewNumber)
						{
							DispatchService.BeginInvoke(() =>
							{
								this.previewBitmapSource = imageSource;
								this.RefreshFromBitmapImage();
							});
						}
					}

					if (this.previewImageWorkQueue.Count == 0)
					{
						workLeft = false;
						this.previewImageQueueProcessing = false;
					}
				}
			}
		}

		private static TransformedBitmap CreateTransformedBitmap(BitmapSource source, VCProfile profile)
		{
			var transformedBitmap = new TransformedBitmap();
			transformedBitmap.BeginInit();
			transformedBitmap.Source = source;
			var transformGroup = new TransformGroup();
			transformGroup.Children.Add(new ScaleTransform(profile.FlipHorizontal ? -1 : 1, profile.FlipVertical ? -1 : 1));
			transformGroup.Children.Add(new RotateTransform(ConvertRotationToDegrees(profile.Rotation)));
			transformedBitmap.Transform = transformGroup;
			transformedBitmap.EndInit();
			transformedBitmap.Freeze();

			return transformedBitmap;
		}

        private static double ConvertRotationToDegrees(VCPictureRotation rotation)
		{
			switch (rotation)
			{
                case VCPictureRotation.None:
					return 0;
                case VCPictureRotation.Clockwise90:
					return 90;
                case VCPictureRotation.Clockwise180:
					return 180;
                case VCPictureRotation.Clockwise270:
					return 270;
			}

			return 0;
		}

		private void RefreshFromBitmapImage()
		{
			if (this.previewBitmapSource == null)
			{
				return;
			}

			if (this.DisplayType != PreviewDisplay.Corners)
			{
				this.PreviewImage = this.previewBitmapSource;
			}

			// In the Corners display mode, the view code will react to the message and read from this.previewBitmapSource.
			Messenger.Default.Send(new PreviewImageChangedMessage());
		}

		private void BackgroundFileSave(object state)
		{
			var job = state as SaveImageJob;

			lock (this.imageSync)
			{
				if (job.UpdateVersion < updateVersion)
				{
					return;
				}
			}

			lock (job.ImageFileSync)
			{
				try
				{
					using (var memoryStream = new MemoryStream())
					{
						// Write the bitmap out to a memory stream before saving so that we won't be holding
						// a write lock on the BitmapImage for very long; it's used in the UI.
						var encoder = new BmpBitmapEncoder();
						encoder.Frames.Add(BitmapFrame.Create(job.Image));
						encoder.Save(memoryStream);

						using (var fileStream = new FileStream(job.FilePath, FileMode.Create))
						{
							fileStream.Write(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
						}
					}
				}
				catch (IOException)
				{
					// Directory may have been deleted. Ignore.
				}
			}
		}
	}
}
