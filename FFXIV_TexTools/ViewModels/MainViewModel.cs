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

using FFXIV_TexTools.Annotations;
using FFXIV_TexTools.Helpers;
using FFXIV_TexTools.Models;
using FFXIV_TexTools.Resources;
using FFXIV_TexTools.Properties;
using FolderSelect;
using MahApps.Metro.Controls.Dialogs;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Helpers;
using xivModdingFramework.Mods;
using xivModdingFramework.SqPack.FileTypes;
using xivModdingFramework.Cache;
using FFXIV_TexTools.Views;
using xivModdingFramework.Mods.DataContainers;

namespace FFXIV_TexTools.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private DirectoryInfo _gameDirectory;
        private readonly MainWindow _mainWindow;

        private ObservableCollection<Category> _categories = new ObservableCollection<Category>();

        private string _searchText, _progressLabel;
        private string _dxVersionText = $"DX: {Properties.Settings.Default.DX_Version}";
        private int _progressValue;
        private Visibility _progressBarVisible, _progressLabelVisible;
        private Index _index;
        private ProgressDialogController _progressController;
        public System.Timers.Timer CacheTimer = new System.Timers.Timer(3000);

        private const string WarningIdentifier = "!!";

        public MainViewModel(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            // This is actually synchronous and can just be called immediately...
            SetDirectories(true);

            _gameDirectory = new DirectoryInfo(Properties.Settings.Default.FFXIV_Directory);
            _index = new Index(_gameDirectory);
            if (ProgressLabel == null)
            {
                ProgressLabel = "";
            }

            // And the rest of this can be pushed into a new thread.
            var task = Task.Run(Initialize);

            // Now we can wait on it.  But we have to thread-safety it.
            task.Wait();

            var exception = task.Exception;
            if(exception != null)
            {
                throw exception;
            }
            var result = task.Result;
            if (!result)
            {
                // We need to die NOW, and not risk any other functions possibly
                // fucking with broken files.
                Process.GetCurrentProcess().Kill();
                return;
            }

            CacheTimer.Elapsed += UpdateDependencyQueueCount;

        }

        public void UpdateDependencyQueueCount(object sender, System.Timers.ElapsedEventArgs e)
        {
            var count = XivCache.GetDependencyQueueLength();
            if (count > 0)
            {
                _mainWindow.ShowStatusMessage("Processing Cache Queue... [" + count + "]");
            }
        }

        /// <summary>
        /// This function is called on a separate thread, *while* the main thread is blocked.
        /// This means a few things.
        ///   1.  You cannot access the view's UI elements (Thread safety error & uninitialized) 
        ///   2.  You cannot use Dispatcher.Invoke (Deadlock)
        ///   3.  You cannot spawn a new full-fledged windows form (Thread safety error) (Basic default popups are OK)
        ///   4.  You cannot shut down the application (Thread safety error)
        ///   
        /// As such, the return value indicates if we want to gracefully shut down the application.
        /// (True for success/continue, False for failure/graceful shutdown.)
        /// 
        /// Exceptions are checked and rethrown on the main thread.
        /// 
        /// This is really 100% only for things that can be safely checked and sanitized
        /// without external code references or UI interaction beyond basic windows dialogs.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> Initialize()
        {

            var success =  await CheckIndexFiles();
            if (!success)
            {
                return false;
            }

            try
            {
                await CheckGameDxVersion();
            } catch
            {
                // Unable to determine version, skip it.
            }

            return true;

        }

        /// <summary>
        /// Checks FFXIV's selected DirectX version and changes TexTools to the appropriate mode if it does not already match.
        /// </summary>
        /// <returns></returns>
        private async Task CheckGameDxVersion()
        {

            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) +
                      "\\My Games\\FINAL FANTASY XIV - A Realm Reborn";

            var DX11 = await Task.Run(() =>
            {
                var dx = false;

                if (File.Exists($"{dir}\\FFXIV_BOOT.cfg"))
                {
                    var lines = File.ReadAllLines($"{dir}\\FFXIV_BOOT.cfg");

                    foreach (var line in lines)
                    {
                        if (line.Contains("DX11Enabled"))
                        {
                            var val = line.Substring(line.Length - 1, 1);
                            if (val.Equals("1"))
                            {
                                dx = true;
                            }

                            break;
                        }
                    }
                }

                return dx;
            });

            if (DX11)
            {
                if (Properties.Settings.Default.DX_Version != "11")
                {
                    // Set the User's DX Mode to 11 in TexTools to match 
                    Properties.Settings.Default.DX_Version = "11";
                    Properties.Settings.Default.Save();
                    DXVersionText = "DX: 11";

                    if(XivCache.Initialized)
                    {
                        var gi = XivCache.GameInfo;
                        XivCache.SetGameInfo(gi.GameDirectory, gi.GameLanguage, 11);
                    }
                }
            }
            else
            {

                if (Properties.Settings.Default.DX_Version != "9")
                {
                    // Set the User's DX Mode to 9 in TexTools to match 
                    var gi = XivCache.GameInfo;
                    Properties.Settings.Default.DX_Version = "9";
                    Properties.Settings.Default.Save();
                    DXVersionText = "DX: 9";
                }

                if (XivCache.Initialized)
                {
                    var gi = XivCache.GameInfo;
                    XivCache.SetGameInfo(gi.GameDirectory, gi.GameLanguage, 9);
                }
            }

        }


        private async Task<bool> CheckIndexFiles()
        {
            var xivDataFiles = new XivDataFile[] { XivDataFile._0A_Exd, XivDataFile._01_Bgcommon, XivDataFile._04_Chara, XivDataFile._06_Ui };
            var problemChecker = new ProblemChecker(_gameDirectory);

            try
            {
                foreach (var xivDataFile in xivDataFiles)
                {
                    var errorFound = await problemChecker.CheckIndexDatCounts(xivDataFile);

                    if (errorFound)
                    {
                        await problemChecker.RepairIndexDatCounts(xivDataFile);
                    }
                }
            }
            catch (Exception ex)
            {
                var result = FlexibleMessageBox.Show("A critical error occurred when attempting to read the FFXIV index files.\n\nWould you like to restore your index backups?\n\nError: " + ex.Message, "Critical Index Error", MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                if (result == DialogResult.Yes)
                {
                    var indexBackupsDirectory = new DirectoryInfo(Settings.Default.Backup_Directory);
                    var success = await problemChecker.RestoreBackups(indexBackupsDirectory);
                    if(!success)
                    {
                        FlexibleMessageBox.Show("Unable to restore Index Backups, shutting down TexTools.", "Critical Error Shutdown", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                        return false;
                    }
                }
                else
                {
                    FlexibleMessageBox.Show("Shutting Down TexTools.", "Critical Error Shutdown",  MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Asks for game directory and sets default save directory
        /// </summary>
        private void SetDirectories(bool valid)
        {
            if (valid)
            {
                var resourceManager = CommonInstallDirectories.ResourceManager;
                var resourceSet = resourceManager.GetResourceSet(CultureInfo.CurrentCulture, true, true);

                if (Properties.Settings.Default.FFXIV_Directory.Equals(""))
                {
                    var saveDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/TexTools/Saved";
                    Directory.CreateDirectory(saveDirectory);
                    Properties.Settings.Default.Save_Directory = saveDirectory;
                    Properties.Settings.Default.Save();

                    var installDirectory = "";
                    foreach (DictionaryEntry commonInstallPath in resourceSet)
                    {
                        if (!Directory.Exists(commonInstallPath.Value.ToString())) continue;

                        if (FlexibleMessageBox.Show(string.Format(UIMessages.InstallDirectoryFoundMessage, commonInstallPath.Value), UIMessages.InstallDirectoryFoundTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            installDirectory = commonInstallPath.Value.ToString();
                            Properties.Settings.Default.FFXIV_Directory = installDirectory;
                            Properties.Settings.Default.Save();
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(installDirectory))
                    {
                        if (FlexibleMessageBox.Show(UIMessages.InstallDirectoryNotFoundMessage, UIMessages.InstallDirectoryNotFoundTitle, MessageBoxButtons.OK, MessageBoxIcon.Question) == DialogResult.OK)
                        {
                            while (!installDirectory.Contains("ffxiv"))
                            {
                                var folderSelect = new FolderSelectDialog()
                                {
                                    Title = UIMessages.SelectffxivFolderTitle
                                };

                                var result = folderSelect.ShowDialog();

                                if (result)
                                {
                                    installDirectory = folderSelect.FileName;
                                }
                                else
                                {
                                    Environment.Exit(0);
                                }
                            }

                            Properties.Settings.Default.FFXIV_Directory = installDirectory;
                            Properties.Settings.Default.Save();
                        }
                        else
                        {
                            Environment.Exit(0);
                        }
                    }
                }

                // Check if it is an old Directory
                var fileLastModifiedTime = File.GetLastWriteTime(
                    $"{Properties.Settings.Default.FFXIV_Directory}\\{XivDataFile._0A_Exd.GetDataFileName()}.win32.dat0");

                if (fileLastModifiedTime.Year < 2020)
                {
                    SetDirectories(false);
                }

                SetSaveDirectory();

                SetBackupsDirectory();

                SetModPackDirectory();

                var modding = new Modding(new DirectoryInfo(Properties.Settings.Default.FFXIV_Directory));
                modding.CreateModlist();
            }
            else
            {
                if (FlexibleMessageBox.Show(UIMessages.OutOfDateInstallMessage, UIMessages.OutOfDateInstallTitle, MessageBoxButtons.OK, MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.OK)
                {
                    var installDirectory = "";

                    while (!installDirectory.Contains("ffxiv"))
                    {
                        var folderSelect = new FolderSelectDialog()
                        {
                            Title = UIMessages.SelectffxivFolderTitle
                        };

                        var result = folderSelect.ShowDialog();

                        if (result)
                        {
                            installDirectory = folderSelect.FileName;
                        }
                        else
                        {
                            Environment.Exit(0);
                        }
                    }

                    // Check if it is an old Directory
                    var fileLastModifiedTime = File.GetLastWriteTime(
                        $"{installDirectory}\\{XivDataFile._0A_Exd.GetDataFileName()}.win32.dat0");

                    if (fileLastModifiedTime.Year < 2019)
                    {
                        SetDirectories(false);
                    }
                    else
                    {
                        Properties.Settings.Default.FFXIV_Directory = installDirectory;
                        Properties.Settings.Default.Save();
                    }
                }
                else
                {
                    Environment.Exit(0);
                }
            }
        }

        private void SetSaveDirectory()
        {
            if (string.IsNullOrEmpty(Properties.Settings.Default.Save_Directory))
            {
                var md = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}/TexTools/Saved";
                Directory.CreateDirectory(md);
                Properties.Settings.Default.Save_Directory = md;
                Properties.Settings.Default.Save();
            }
            else
            {
                if (!Directory.Exists(Properties.Settings.Default.Save_Directory))
                {
                    Directory.CreateDirectory(Properties.Settings.Default.Save_Directory);
                }
            }
        }

        private void SetBackupsDirectory()
        {
            if (string.IsNullOrEmpty(Properties.Settings.Default.Backup_Directory))
            {
                var md = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}/TexTools/Index_Backups";
                Directory.CreateDirectory(md);
                Properties.Settings.Default.Backup_Directory = md;
                Properties.Settings.Default.Save();
            }
            else
            {
                if (!Directory.Exists(Properties.Settings.Default.Backup_Directory))
                {
                    Directory.CreateDirectory(Properties.Settings.Default.Backup_Directory);
                }
            }
        }

        private void SetModPackDirectory()
        {
            if (string.IsNullOrEmpty(Properties.Settings.Default.ModPack_Directory))
            {
                var md = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/TexTools/ModPacks";
                Directory.CreateDirectory(md);
                Properties.Settings.Default.ModPack_Directory = md;
                Properties.Settings.Default.Save();
            }
            else
            {
                if (!Directory.Exists(Properties.Settings.Default.ModPack_Directory))
                {
                    Directory.CreateDirectory(Properties.Settings.Default.ModPack_Directory);
                }
            }
        }


        /// <summary>
        /// Performs post-patch modlist corrections and validation, prompting user also to generate backups after a successful completion.
        /// </summary>
        /// <returns></returns>
        public async Task DoPostPatchCleanup()
        {

            FlexibleMessageBox.Show(_mainWindow.Win32Window, UIMessages.PatchDetectedMessage, "Post Patch Cleanup Starting", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            MainWindow.MakeHighlander();


            await _mainWindow.LockUi("Performing Post-Patch Maintenence", "This may take a few minutes if you have many mods installed.", this);
            try
            {
                var modding = new Modding(_gameDirectory);
                var _index = new Index(_gameDirectory);
                var _dat = new Dat(_gameDirectory);

                // We have to do a few things here.
                // 1.  Save a list of what mods were enabled.
                // 2.  Go through and validate everything that says it is enabled actually is enabled, or mark it as disabled and update its original index offset if it is not.
                // 3.  Prompt the user for either a full disable and backup creation, or a restore to normal state (re-enable anything that was enabled before but is not now)
                var modList = modding.GetModList();


                var internalFilesModified = false;

                // Cache our currently enabled stuff.
                List<Mod> enabledMods = modList.Mods.Where(x => x.enabled == true).ToList();
                var toRemove = new List<Mod>();

                foreach (var mod in modList.Mods)
                {
                    if (!String.IsNullOrEmpty(mod.fullPath))
                    {
                        var index1Value = await _index.GetDataOffset(mod.fullPath);
                        var index2Value = await _index.GetDataOffsetIndex2(mod.fullPath);
                        var oldOriginalOffset = mod.data.originalOffset;
                        var modOffset = mod.data.modOffset;

                        // In any event where an offset does not match either of our saved offsets, we must assume this is a new
                        // default file offset for post-patch.
                        if (index1Value != oldOriginalOffset && index1Value != modOffset && index1Value != 0)
                        {
                            // Index 1 value is our new base offset.
                            mod.data.originalOffset = index1Value;
                            mod.enabled = false;
                        }
                        else if (index2Value != oldOriginalOffset && index2Value != modOffset && index2Value != 0)
                        {
                            // Index 2 value is our new base offset.
                            mod.data.originalOffset = index2Value;
                            mod.enabled = false;
                        }

                        // Indexes don't match.  This can occur if SE adds something to index2 that didn't exist in index2 before.
                        if (index1Value != index2Value && index2Value != 0)
                        {
                            if(mod.source == Constants.InternalModSourceName)
                            {
                                internalFilesModified = true;
                            }

                            // We should never actually get to this state for file-addition mods.  If we do, uh.. I guess correct the indexes and yolo?
                            await _index.UpdateDataOffset(mod.data.originalOffset, mod.fullPath, false);
                            index1Value = mod.data.originalOffset;
                            index2Value = mod.data.originalOffset;

                            mod.enabled = false;
                        }

                        // Set it to the corrected state.
                        if (index1Value == mod.data.modOffset)
                        {
                            mod.enabled = true;
                        }
                        else
                        {
                            mod.enabled = false;
                        }


                        // Perform a basic file type check on our results.
                        var fileType = _dat.GetFileType(mod.data.modOffset, IOUtil.GetDataFileFromPath(mod.fullPath));
                        var originalFileType = _dat.GetFileType(mod.data.modOffset, IOUtil.GetDataFileFromPath(mod.fullPath));

                        var validTypes = new List<int>() { 2, 3, 4 };
                        if (!validTypes.Contains(fileType))
                        {
                            // Mod data is busted.  Fun.
                            toRemove.Add(mod);
                            if (mod.source == Constants.InternalModSourceName)
                            {
                                internalFilesModified = true;
                            }
                        }

                        if (!validTypes.Contains(originalFileType))
                        {
                            if (mod.IsCustomFile())
                            {
                                // Okay, in this case this is recoverable as the mod is a custom addition anyways, so we can just delete it.
                            }
                            else
                            {
                                // Update ended up with us unable to find a valid original offset.  Double fun.
                                throw new Exception("Unable to determine working offset for file:" + mod.fullPath);
                            }
                        }
                    }

                    // Okay, this mod is now represented in the modlist in it's actual post-patch index state.
                    var datNum = (int)((mod.data.modOffset / 8) & 0x0F) / 2;
                    var dat = XivDataFiles.GetXivDataFile(mod.datFile);

                    var originalDats = await _dat.GetUnmoddedDatList(dat);
                    var datPath = $"{dat.GetDataFileName()}{Dat.DatExtension}{datNum}";

                    // Test for SE Dat file rollover.
                    if (originalDats.Contains(datPath))
                    {
                        // Shit.  This means that the dat file where this mod lived got eaten by SE.  We have to destroy the modlist entry at this point.
                        toRemove.Add(mod);
                    }

                    if (mod.enabled == false && mod.source == Constants.InternalModSourceName && !String.IsNullOrEmpty(mod.fullPath))
                    {
                        // Shit.  Some internal multi-edit file got eaten.  This means we'll have to re-apply all metadata mods later.
                        internalFilesModified = true;
                    }
                }

                modding.SaveModList(modList);

                if (toRemove.Count > 0)
                {
                    var removedString = "";
                    foreach (var mod in toRemove)
                    {
                        if (mod.enabled)
                        {
                            // We shouldn't really get here with something like this enabled, but if it is, disable it.
                            await modding.ToggleModUnsafe(false, mod, true);
                            mod.enabled = false;
                        }

                        modList.Mods.Remove(mod);

                        // Since we're deleting this entry entirely, we can't leave it in the other cached list either to get re-enabled later.
                        enabledMods.Remove(mod);

                        removedString += mod.fullPath + "\n";
                    }

                    modding.SaveModList(modList);

                    // Show the user a message if we purged any real files.
                    if (toRemove.Any(x => !String.IsNullOrEmpty(x.fullPath)))
                    {
                        var text = String.Format(UIMessages.PatchDestroyedFiles, removedString);

                        FlexibleMessageBox.Show(_mainWindow.Win32Window, text, "Destroyed Files Notification", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                    }
                }

                var result = FlexibleMessageBox.Show(_mainWindow.Win32Window, UIMessages.PostPatchBackupPrompt, "Post-Patch Backup Prompt", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);

                if (result == DialogResult.Yes)
                {
                    _mainWindow.LockProgress.Report("Disabling Mods.  This can take a minute if you have many mods...");
                    await modding.ToggleAllMods(false);

                    _mainWindow.LockProgress.Report("Creating Index Backups...");
                    var pc = new ProblemChecker(_gameDirectory);
                    DirectoryInfo backupDir;
                    try
                    {
                        Directory.CreateDirectory(Settings.Default.Backup_Directory);
                        backupDir = new DirectoryInfo(Settings.Default.Backup_Directory);
                    }
                    catch
                    {
                        throw new Exception("Unable to create index backups.\nThe Index Backup directory is invalid or inaccessible: " + Settings.Default.Backup_Directory);
                    }

                    await pc.BackupIndexFiles(backupDir);

                    FlexibleMessageBox.Show(_mainWindow.Win32Window, UIMessages.PostPatchBackupsComplete, "Post-Patch Backup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);

                }
                else
                {
                    _mainWindow.LockProgress.Report("Re-Enabling mods disabled by FFXIV Patch...");

                    if (internalFilesModified)
                    {
                        // Okay, if any of our internal multi-edit files got edited, we have to bash them all so that we can rebuild them from the new game files correctly.
                        var internals = modList.Mods.Where(x => x.IsInternal());
                        foreach(var mod in internals)
                        {
                            await modding.DeleteMod(mod.fullPath, true);
                        }

                        // And likewise, set the raw meta entries to disabled to ensure they get re-enabled fully.
                        var metas = modList.Mods.Where(x => Path.GetExtension(x.fullPath) == ".meta");
                        foreach(var mod in metas)
                        {
                            await _index.DeleteFileDescriptor(mod.fullPath, IOUtil.GetDataFileFromPath(mod.fullPath));
                            mod.enabled = false;
                        }
                    }

                    foreach (var mod in enabledMods)
                    {
                        // If the mod was disabled by our previous 
                        if(!mod.enabled)
                        {
                            await modding.ToggleModStatus(mod.fullPath, true);
                        }
                    }


                    FlexibleMessageBox.Show(_mainWindow.Win32Window, UIMessages.PostPatchComplete, "Post-Patch Process Complete", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);

                }
            }
            catch(Exception Ex)
            {
                // Show the user the error, then let them go about their business of fixing things.
                FlexibleMessageBox.Show(_mainWindow.Win32Window, String.Format(UIMessages.PostPatchError, Ex.Message), "Post-Patch Failure", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            }
            finally
            {
                await _mainWindow.UnlockUi(this);
            }
        }


        /// <summary>
        /// The DX Version
        /// </summary>
        public string DXVersionText
        {
            get => _dxVersionText;
            set
            {
                _dxVersionText = value;
                NotifyPropertyChanged(nameof(DXVersionText));
            }
        }

        /// <summary>
        /// The list of categories
        /// </summary>
        public ObservableCollection<Category> Categories
        {
            get => _categories;
            set
            {
                _categories = value;
                NotifyPropertyChanged(nameof(Categories));
            }
        }

        /// <summary>
        /// The text from the search box
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                NotifyPropertyChanged(nameof(SearchText));
            }
        }

        /// <summary>
        /// The value for the progressbar
        /// </summary>
        public int ProgressValue
        {
            get => _progressValue;
            set
            {
                _progressValue = value;
                NotifyPropertyChanged(nameof(ProgressValue));
            }
        }

        /// <summary>
        /// The text for the progress label
        /// </summary>
        public string ProgressLabel
        {
            get => _progressLabel;
            set
            {
                _progressLabel = value;
                NotifyPropertyChanged(nameof(ProgressLabel));
            }
        }

        public Visibility ProgressBarVisible
        {
            get => _progressBarVisible;
            set
            {
                _progressBarVisible = value;
                NotifyPropertyChanged(nameof(ProgressBarVisible));
            }
        }

        public Visibility ProgressLabelVisible
        {
            get => _progressLabelVisible;
            set
            {
                _progressLabelVisible = value;
                NotifyPropertyChanged(nameof(ProgressLabelVisible));
            }
        }

        #region MenuItems

        public ICommand DXVersionCommand => new RelayCommand(SetDXVersion);
        public ICommand EnableAllModsCommand => new RelayCommand(EnableAllMods);
        public ICommand DisableAllModsCommand => new RelayCommand(DisableAllMods);

        /// <summary>
        /// Sets the DX version for the application
        /// </summary>
        private void SetDXVersion(object obj)
        {
            var gi = XivCache.GameInfo;
            if (DXVersionText.Contains("11"))
            {
                Properties.Settings.Default.DX_Version = "9";
                Properties.Settings.Default.Save();
                XivCache.SetGameInfo(gi.GameDirectory, gi.GameLanguage, 9);
            }
            else
            {
                Properties.Settings.Default.DX_Version = "11";
                Properties.Settings.Default.Save();
                XivCache.SetGameInfo(gi.GameDirectory, gi.GameLanguage, 11);
            }

            DXVersionText = $"DX: {Properties.Settings.Default.DX_Version}";
        }



        /// <summary>
        /// Enables all mods in the mod list
        /// </summary>
        /// <param name="obj"></param>
        private async void EnableAllMods(object obj)
        {
            if (_index.IsIndexLocked(XivDataFile._0A_Exd))
            {
                FlexibleMessageBox.Show(UIMessages.IndexLockedErrorMessage, UIMessages.IndexLockedErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);

                return;
            }

            _progressController = await _mainWindow.ShowProgressAsync(UIMessages.EnablingModsTitle, UIMessages.PleaseWaitMessage);
            var progressIndicator = new Progress<(int current, int total, string message)>(ReportProgress);

            if (FlexibleMessageBox.Show(
                    UIMessages.EnableAllModsMessage, UIMessages.EnablingModsTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var modding = new Modding(_gameDirectory);
                bool err = false;
                try
                {
                    await modding.ToggleAllMods(true, progressIndicator);
                } catch(Exception ex)
                {
                    FlexibleMessageBox.Show("Failed to Enable all Mods: \n\nError:" + ex.Message, "Enable Mod Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    err = true;
                }

                await _progressController.CloseAsync();
                if (!err)
                {
                    await _mainWindow.ShowMessageAsync(UIMessages.SuccessTitle, UIMessages.ModsEnabledSuccessMessage);
                }
            }
            else
            {
                await _progressController.CloseAsync();
            }
        }

        /// <summary>
        /// Disables all mods in the mod list
        /// </summary>
        private async void DisableAllMods(object obj)
        {
            if (_index.IsIndexLocked(XivDataFile._0A_Exd))
            {
                FlexibleMessageBox.Show(UIMessages.IndexLockedErrorMessage, UIMessages.IndexLockedErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);

                return;
            }

            _progressController = await _mainWindow.ShowProgressAsync(UIMessages.DisablingModsTitle, UIMessages.PleaseWaitMessage);
            var progressIndicator = new Progress<(int current, int total, string message)>(ReportProgress);

            if (FlexibleMessageBox.Show(
                    UIMessages.DisableAllModsMessage, UIMessages.DisableAllModsTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var modding = new Modding(_gameDirectory);
                bool err = false;
                try { 
                    await modding.ToggleAllMods(false, progressIndicator);
                } catch (Exception ex)
                {
                    FlexibleMessageBox.Show("Failed to Disable all Mods: \n\nError:" + ex.Message, "Disable Mod Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    err = true;
                }

                await _progressController.CloseAsync();

                if (!err)
                {
                    await _mainWindow.ShowMessageAsync(UIMessages.SuccessTitle, UIMessages.ModsDisabledSuccessMessage);
                }
            }
            else
            {
                await _progressController.CloseAsync();
            }

        }

        /// <summary>
        /// Updates the progress bar
        /// </summary>
        /// <param name="value">The progress value</param>
        private void ReportProgress((int current, int total, string message) report)
        {
            if (!report.message.Equals(string.Empty))
            {
                _progressController.SetMessage(report.message);
                _progressController.SetIndeterminate();
            }
            else
            {
                _progressController.SetMessage(
                    $"{UIMessages.PleaseStandByMessage} ({report.current} / {report.total})");

                var value = (double)report.current / (double)report.total;
                _progressController.SetProgress(value);
            }
        }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

    }
}