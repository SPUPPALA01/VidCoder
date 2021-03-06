﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using HandBrake.Interop.Model;
using System.Windows.Input;
using HandBrake.Interop.Model.Encoding;
using VidCoder.DragDropUtils;
using HandBrake.Interop;
using System.Diagnostics;
using VidCoder.Messages;
using VidCoder.Model;
using VidCoder.Model.Encoding;
using VidCoder.ViewModel.Components;

namespace VidCoder.ViewModel
{
	using System.Globalization;
	using Resources;
	using Services;

	public class EncodeJobViewModel : ViewModelBase, IDragItem
	{
		public const double SubtitleScanCostFactor = 5.0;

		private MainViewModel main = Ioc.Container.GetInstance<MainViewModel>();
		private ProcessingViewModel processingVM;

		private bool isSelected;
		private bool isPaused;
		private VCJob job;
		private bool encoding;
		private int percentComplete;
		private bool isOnlyItem;
		private Stopwatch encodeTimeStopwatch;
		private TimeSpan eta;

		public EncodeJobViewModel(VCJob job)
		{
			this.job = job;

			Messenger.Default.Register<ScanningChangedMessage>(
				this,
				message =>
					{
						this.EditQueueJobCommand.RaiseCanExecuteChanged();
					});
		}

		public VCJob Job
		{
			get
			{
				return this.job;
			}
		}

		public VCProfile Profile
		{
			get
			{
				return this.Job.EncodingProfile;
			}
		}

		public MainViewModel MainViewModel
		{
			get
			{
				return this.main;
			}
		}

		public ProcessingViewModel ProcessingVM
		{
			get
			{
				if (this.processingVM == null)
				{
					this.processingVM = Ioc.Container.GetInstance<ProcessingViewModel>();
				}

				return this.processingVM;
			}
		}

		public ILogger Logger { get; set; }

		public HandBrakeInstance HandBrakeInstance { get; set; }

		public VideoSource VideoSource { get; set; }

		public VideoSourceMetadata VideoSourceMetadata { get; set; }

		// The parent folder for the item (if it was inside a folder of files added in a batch)
		public string SourceParentFolder { get; set; }

		// True if the output path was picked manually rather than auto-generated
		public bool ManualOutputPath { get; set; }

		public string NameFormatOverride { get; set; }

		public string PresetName { get; set; }

		public bool IsSelected
		{
			get
			{
				return this.isSelected;
			}

			set
			{
				this.isSelected = value;
				this.RaisePropertyChanged(() => this.IsSelected);
			}
		}

		public bool Encoding
		{
			get
			{
				return this.encoding;
			}

			set
			{
				this.encoding = value;
				this.EditQueueJobCommand.RaiseCanExecuteChanged();
				this.RemoveQueueJobCommand.RaiseCanExecuteChanged();
				this.RaisePropertyChanged(() => this.Encoding);
			}
		}

		public System.Windows.Media.Brush ProgressBarColor
		{
			get
			{
				if (this.isPaused)
				{
					return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 230, 0));
				}
				else
				{
					return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 200, 0));
				}
			}
		}

		public bool IsOnlyItem
		{
			get
			{
				return this.isOnlyItem;
			}

			set
			{
				this.isOnlyItem = value;
				this.RaisePropertyChanged(() => this.ShowProgressBar);
			}
		}

		/// <summary>
		/// Returns true if a subtitle scan will be performed on this job.
		/// </summary>
		public bool SubtitleScan
		{
			get
			{
				if (this.Job.Subtitles == null)
				{
					return false;
				}

				if (this.Job.Subtitles.SourceSubtitles == null)
				{
					return false;
				}

				return this.Job.Subtitles.SourceSubtitles.Count(item => item.TrackNumber == 0) > 0;
			}
		}

		/// <summary>
		/// Gets the job cost. Cost is roughly proportional to the amount of time it takes to encode 1 second
		/// of video.
		/// </summary>
		public double Cost
		{
			get
			{
				double cost = this.Job.Length.TotalSeconds;

                if (this.Job.EncodingProfile.VideoEncodeRateType != VCVideoEncodeRateType.ConstantQuality && this.Job.EncodingProfile.TwoPass)
				{
					cost += this.Job.Length.TotalSeconds;
				}

				if (this.SubtitleScan)
				{
					cost += this.Job.Length.TotalSeconds / SubtitleScanCostFactor;
				}

				return cost;
			}
		}

		public TimeSpan EncodeTime
		{
			get
			{
				if (this.encodeTimeStopwatch == null)
				{
					return TimeSpan.Zero;
				}

				return this.encodeTimeStopwatch.Elapsed;
			}
		}

		public bool ShowQueueEditButtons
		{
			get
			{
				return !this.encoding;
			}
		}

		public bool ShowProgressBar
		{
			get
			{
				return this.encoding && !this.IsOnlyItem;
			}
		}

		public TimeSpan Eta
		{
			get
			{
				return this.eta;
			}

			set
			{
				this.eta = value;
				this.RaisePropertyChanged(() => this.Eta);
				this.RaisePropertyChanged(() => this.ProgressToolTip);
			}
		}

		public string ProgressToolTip
		{
			get
			{
				if (this.Eta == TimeSpan.Zero)
				{
					return null;
				}

				return "Job ETA: " + Utilities.FormatTimeSpan(this.Eta);
			}
		}

		public int PercentComplete
		{
			get
			{
				return this.percentComplete;
			}

			set
			{
				this.percentComplete = value;
				this.RaisePropertyChanged(() => this.PercentComplete);
			}
		}

		public string RangeDisplay
		{
			get
			{
				switch (this.job.RangeType)
				{
					case VideoRangeType.All:
						return MainRes.QueueRangeAll;
					case VideoRangeType.Chapters:
						int startChapter = this.job.ChapterStart;
						int endChapter = this.job.ChapterEnd;

						string chaptersString;
						if (startChapter == endChapter)
						{
							chaptersString = startChapter.ToString(CultureInfo.CurrentCulture);
						}
						else
						{
							chaptersString = startChapter + " - " + endChapter;
						}

						return string.Format(MainRes.QueueFormat_Chapters, chaptersString);
					case VideoRangeType.Seconds:
						return TimeSpan.FromSeconds(this.job.SecondsStart).ToString("g") + " - " + TimeSpan.FromSeconds(this.job.SecondsEnd).ToString("g");
					case VideoRangeType.Frames:
						return string.Format(MainRes.QueueFormat_Frames, this.job.FramesStart, this.job.FramesEnd);
					default:
						throw new ArgumentOutOfRangeException();
				}
			}
		}

		public string VideoEncoderDisplay
		{
			get
			{
				return Encoders.GetVideoEncoder(this.Profile.VideoEncoder).DisplayName;
			}
		}

		public string AudioEncodersDisplay
		{
			get
			{
				var encodingParts = new List<string>();
				foreach (AudioEncoding audioEncoding in this.Profile.AudioEncodings)
				{
					encodingParts.Add(Encoders.GetAudioEncoder(audioEncoding.Encoder).DisplayName);
				}

				return string.Join(", ", encodingParts);
			}
		}

		public string VideoQualityDisplay
		{
			get
			{
				switch (this.Profile.VideoEncodeRateType)
				{
					case VCVideoEncodeRateType.AverageBitrate:
						return this.Profile.VideoBitrate + " kbps";
                    case VCVideoEncodeRateType.TargetSize:
						return this.Profile.TargetSize + " MB";
                    case VCVideoEncodeRateType.ConstantQuality:
						return "CQ " + this.Profile.Quality;
					default:
						break;
				}

				return string.Empty;
			}
		}

		public string DurationDisplay
		{
			get
			{
				TimeSpan length = this.Job.Length;

				return string.Format("{0}:{1:00}:{2:00}", Math.Floor(length.TotalHours), length.Minutes, length.Seconds);
			}
		}

		public string AudioQualityDisplay
		{
			get
			{
				var bitrateParts = new List<string>();
				foreach (AudioEncoding audioEncoding in this.Profile.AudioEncodings)
				{
					if (!Encoders.GetAudioEncoder(audioEncoding.Encoder).IsPassthrough)
					{
						if (audioEncoding.EncodeRateType == AudioEncodeRateType.Bitrate)
						{
							if (audioEncoding.Bitrate == 0)
							{
								bitrateParts.Add(MainRes.CbrAuto);
							}
							else
							{
								bitrateParts.Add(audioEncoding.Bitrate + " kbps");
							}
						}
						else
						{
							bitrateParts.Add("CQ " + audioEncoding.Quality);
						}
					}
				}

				return string.Join(", ", bitrateParts);
			}
		}

		private RelayCommand editQueueJobCommand;
		public RelayCommand EditQueueJobCommand
		{
			get
			{
				return this.editQueueJobCommand ?? (this.editQueueJobCommand = new RelayCommand(() =>
					{
						this.main.EditJob(this);
					},
					() =>
					{
						return !this.Encoding && !this.main.ScanningSource;
					}));
			}
		}

		private RelayCommand removeQueueJobCommand;
		public RelayCommand RemoveQueueJobCommand
		{
			get
			{
				return this.removeQueueJobCommand ?? (this.removeQueueJobCommand = new RelayCommand(() =>
					{
						this.ProcessingVM.RemoveQueueJob(this);
					},
					() =>
					{
						return !this.Encoding;
					}));
			}
		}

		public bool CanDrag
		{
			get
			{
				return !this.Encoding;
			}
		}

		public void ReportEncodeStart(bool isOnlyItem)
		{
			this.Encoding = true;
			this.IsOnlyItem = isOnlyItem;
			this.encodeTimeStopwatch = Stopwatch.StartNew();
			this.RaisePropertyChanged(() => this.ShowQueueEditButtons);
			this.RaisePropertyChanged(() => this.ShowProgressBar);
		}

		public void ReportEncodePause()
		{
			this.isPaused = true;
			this.encodeTimeStopwatch.Stop();
			this.RaisePropertyChanged(() => this.ProgressBarColor);
		}

		public void ReportEncodeResume()
		{
			this.isPaused = false;
			this.encodeTimeStopwatch.Start();
			this.RaisePropertyChanged(() => this.ProgressBarColor);
		}

		public void ReportEncodeEnd()
		{
			this.Encoding = false;
			this.encodeTimeStopwatch.Stop();
			this.RaisePropertyChanged(() => this.ShowQueueEditButtons);
			this.RaisePropertyChanged(() => this.ShowProgressBar);
		}
	}
}
