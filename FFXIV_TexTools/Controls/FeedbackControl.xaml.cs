﻿// FFXIV TexTools
// Copyright © 2019 Rafael Gonzalez - All Rights Reserved
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Windows.Controls;
using MahApps.Metro.IconPacks;
using System.Windows;
using System;
using System.Threading.Tasks;
using System.Windows.Media.Animation;

namespace FFXIV_TexTools.Controls
{
	/// <summary>
	/// Interaction logic for FeedbackControl.xaml
	/// </summary>
	public partial class FeedbackControl : UserControl
	{
		public FeedbackControl()
		{
			InitializeComponent();
		}

		public void Show(PackIconFontAwesomeKind icon, double progress = -1)
		{
			this.Area.Visibility = Visibility.Visible;
			this.Icon.Kind = icon;

			if (progress < 0)
			{
				this.Progress.IsIndeterminate = true;
			}
			else
			{
				this.Progress.IsIndeterminate = false;
				this.Progress.Value = progress * 100;
			}

			Storyboard storyboard;
			if (this.Area.Opacity <= 0)
			{
				storyboard = this.Resources["FadeInOut"] as Storyboard;
			}
			else
			{
				storyboard = this.Resources["FadeOut"] as Storyboard;
			}

			storyboard.Begin();
		}
	}
}
