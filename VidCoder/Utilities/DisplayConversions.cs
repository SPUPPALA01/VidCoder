﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HandBrake.Interop.Model.Encoding;
using HandBrake.Interop.SourceData;
using VidCoder.Model;
using HandBrake.Interop;

namespace VidCoder
{
	public static class DisplayConversions
	{
		private static EnumStringConverter<AudioEncoder> audioConverter = new EnumStringConverter<AudioEncoder>();
		private static EnumStringConverter<Mixdown> mixdownConverter = new EnumStringConverter<Mixdown>();
		private static EnumStringConverter<InputType> inputTypeConverter = new EnumStringConverter<InputType>();

		public static string DisplayAudioEncoder(AudioEncoder audioEncoder)
		{
			return audioConverter.Convert(audioEncoder);
		}

		public static string DisplayMixdown(Mixdown mixdown)
		{
			return mixdownConverter.Convert(mixdown);
		}

		public static string DisplayInputType(InputType inputType)
		{
			return inputTypeConverter.Convert(inputType);
		}

		public static string DisplayVideoCodecName(string videoCodecName)
		{
			switch (videoCodecName)
			{
				case "h264":
					return "H.264";
				case "mpeg2":
				case "mpeg2video":
					return "MPEG-2";
			}

			return videoCodecName;
		}

		public static string DisplaySampleRate(int sampleRate)
		{
			switch (sampleRate)
			{
				case 48000:
					return "48 kHz";
				case 44100:
					return "44.1 kHz";
				case 32000:
					return "32 kHz";
				case 24000:
					return "24 kHz";
				case 22050:
					return "22.05 kHz";
				case 16000:
					return "16 kHz";
				case 12000:
					return "12 kHz";
				case 11025:
					return "11.025 kHz";
				case 8000:
					return "8 kHz";
			}

			return sampleRate.ToString();
		}
	}
}
