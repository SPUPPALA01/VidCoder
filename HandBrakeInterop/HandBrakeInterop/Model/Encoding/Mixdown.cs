﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;

namespace HandBrake.Interop
{
	public enum Mixdown
	{
		[Display(Name = "Dolby Pro Logic II")]
		DolbyProLogicII = 0,

		[Display(Name = "Mono")]
		Mono,

		[Display(Name = "Stereo")]
		Stereo,

		[Display(Name = "Dolby Surround")]
		DolbySurround,

		[Display(Name = "6 Channel Discrete")]
		SixChannelDiscrete
	}
}
