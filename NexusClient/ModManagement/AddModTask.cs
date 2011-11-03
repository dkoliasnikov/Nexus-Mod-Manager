﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Nexus.Client.BackgroundTasks;
using Nexus.Client.DownloadManagement;
using Nexus.Client.Games;
using Nexus.Client.ModAuthoring;
using Nexus.Client.ModRepositories;
using Nexus.Client.Mods;
using Nexus.Client.Settings;
using Nexus.Client.Util;

namespace Nexus.Client.ModManagement
{
	/// <summary>
	/// Adds, and downloads if required, a mod to the mod manager.
	/// </summary>
	public class AddModTask : BackgroundTask, IDisposable
	{
		private IGameMode m_gmdGameMode = null;
		private IEnvironmentInfo m_eifEnvironmentInfo = null;
		private IModRepository m_mrpModRepository = null;
		private IModFormatRegistry m_mfrModFormatRegistry = null;
		private ConfirmOverwriteCallback m_cocConfirmOverwrite = null;
		private Dictionary<IBackgroundTask, Int32> m_dicLastProgress = new Dictionary<IBackgroundTask, Int32>();
		private List<IBackgroundTask> m_lstRunningTasks = new List<IBackgroundTask>();
		private bool m_booFinishedDownloads = false;
		private Int32 m_intOverallProgressOffset = 0;
		private Uri m_uriPath = null;

		#region Properties

		/// <summary>
		/// Gets the descriptor describing the mod being added.
		/// </summary>
		/// <value>The descriptor describing the mod being added.</value>
		protected AddModDescriptor Descriptor { get; private set; }

		/// <summary>
		/// Gets the metadata about the mod we are adding.
		/// </summary>
		/// <value>The metadata about the mod we are adding.</value>
		public IModInfo ModInfo { get; private set; }

		/// <summary>
		/// Gets whether the task supports pausing.
		/// </summary>
		/// <value>Thether the task supports pausing.</value>
		public override bool SupportsPause
		{
			get
			{
				return true;
			}
		}

		#endregion

		#region Constructors

		/// <summary>
		/// A simple constructor that initializes the object with the given values.
		/// </summary>
		/// <param name="p_gmdGameMode">The game mode for which mods are being managed.</param>
		/// <param name="p_eifEnvironmentInfo">The application's envrionment info.</param>
		/// <param name="p_frgFormatRegistry">The <see cref="IModFormatRegistry"/> that contains the list
		/// of supported <see cref="IModFormat"/>s.</param>
		/// <param name="p_mrpModRepository">The mod repository from which to get mods and mod metadata.</param>
		/// <param name="p_uriPath">The path to the mod to add.</param>
		/// <param name="p_cocConfirmOverwrite">The delegate to call to resolve conflicts with existing files.</param>
		public AddModTask(IGameMode p_gmdGameMode, IEnvironmentInfo p_eifEnvironmentInfo, IModFormatRegistry p_frgFormatRegistry, IModRepository p_mrpModRepository, Uri p_uriPath, ConfirmOverwriteCallback p_cocConfirmOverwrite)
		{
			m_gmdGameMode = p_gmdGameMode;
			m_eifEnvironmentInfo = p_eifEnvironmentInfo;
			m_mfrModFormatRegistry = p_frgFormatRegistry;
			m_mrpModRepository = p_mrpModRepository;
			m_uriPath = p_uriPath;
			m_cocConfirmOverwrite = p_cocConfirmOverwrite;
		}

		#endregion

		/// <summary>
		/// Gets the name of the mod to use for display.
		/// </summary>
		/// <returns>The name of the mod to use for display.</returns>
		private string GetModDisplayName()
		{
			if (String.IsNullOrEmpty(ModInfo.ModName))
				return Path.GetFileNameWithoutExtension(Descriptor.DefaultSourcePath);
			return ModInfo.ModName;
		}

		/// <summary>
		/// Starts the mod adding task.
		/// </summary>
		public void AddMod()
		{
			Trace.TraceInformation(String.Format("[{0}] Starting Add Mod Task.", m_uriPath));
			Status = TaskStatus.Running;
			OverallProgress = 0;
			OverallProgressStepSize = 1;
			ShowItemProgress = true;
			OverallMessage = String.Format("Adding {0}...", m_uriPath);

			Descriptor = BuildDescriptor(m_uriPath);

			ModInfo = GetModInfo(Descriptor);

			OverallMessage = String.Format("Adding {0}...", GetModDisplayName());

			if (Descriptor.Status == TaskStatus.Paused)
				Pause();
			else
			{
				if (Descriptor.DownloadFiles.IsNullOrEmpty())
				{
					OverallProgressMaximum = 4;
					AddModFile(m_cocConfirmOverwrite);
				}
				else
				{
					m_intOverallProgressOffset = 1;
					OverallProgressMaximum = 5;
					ItemProgressMaximum = 0;
					ItemMessage = String.Format("Downloading {0}...", GetModDisplayName());
					DownloadFiles(Descriptor.DownloadFiles);
				}
			}
		}

		/// <summary>
		/// Get the reposiroty info for the described mod.
		/// </summary>
		/// <param name="p_amdDescriptor">The obejct that describes the mod for which to retrieve the info.</param>
		/// <returns>The repository info for the described mod.</returns>
		private IModInfo GetModInfo(AddModDescriptor p_amdDescriptor)
		{
			switch (p_amdDescriptor.SourceUri.Scheme.ToLowerInvariant())
			{
				case "file":
					return new ModInfo(m_mrpModRepository.GetModInfoForFile(Path.GetFileName(p_amdDescriptor.DefaultSourcePath)));
				case "nxm":
					NexusUrl nxuModUrl = new NexusUrl(p_amdDescriptor.SourceUri);
					if (String.IsNullOrEmpty(nxuModUrl.ModId))
						throw new ArgumentException("Invalid Nexus URI: " + p_amdDescriptor.SourceUri.ToString());
					IModInfo mifInfo = m_mrpModRepository.GetModInfo(nxuModUrl.ModId);
					IModFileInfo mfiFileInfo = m_mrpModRepository.GetFileInfo(nxuModUrl.ModId, nxuModUrl.FileId);
					mifInfo = AutoTagger.CombineInfo(mifInfo, mfiFileInfo);
					return mifInfo;
				default:
					Trace.TraceInformation(String.Format("[{0}] Can't get mod info.", p_amdDescriptor.SourceUri.ToString()));
					throw new Exception("Unable to retrieve nod info: " + p_amdDescriptor.SourceUri.ToString());
			}
		}

		/// <summary>
		/// Build the obejct that describes the mod being added.
		/// </summary>
		/// <param name="p_uriPath">The path of the mod being added.</param>
		/// <returns>The obejct that describes the mod being added.</returns>
		private AddModDescriptor BuildDescriptor(Uri p_uriPath)
		{
			if (!m_eifEnvironmentInfo.Settings.QueuedModsToAdd.ContainsKey(m_gmdGameMode.ModeId))
				m_eifEnvironmentInfo.Settings.QueuedModsToAdd[m_gmdGameMode.ModeId] = new KeyedSettings<AddModDescriptor>();
			KeyedSettings<AddModDescriptor> dicQueuedMods = m_eifEnvironmentInfo.Settings.QueuedModsToAdd[m_gmdGameMode.ModeId];
			AddModDescriptor amdDescriptor = null;
			if (!dicQueuedMods.TryGetValue(p_uriPath.ToString(), out amdDescriptor))
			{
				switch (p_uriPath.Scheme.ToLowerInvariant())
				{
					case "file":
						amdDescriptor = new AddModDescriptor(p_uriPath, p_uriPath.LocalPath, null, TaskStatus.Running);
						break;
					case "nxm":
						NexusUrl nxuModUrl = new NexusUrl(p_uriPath);

						if (String.IsNullOrEmpty(nxuModUrl.ModId))
							throw new ArgumentException("Invalid Nexus URI: " + p_uriPath.ToString());

						IModFileInfo mfiFile = null;
						if (String.IsNullOrEmpty(nxuModUrl.FileId))
							mfiFile = m_mrpModRepository.GetDefaultFileInfo(nxuModUrl.ModId);
						else
							mfiFile = m_mrpModRepository.GetFileInfo(nxuModUrl.ModId, nxuModUrl.FileId);
						if (mfiFile == null)
						{
							Trace.TraceInformation(String.Format("[{0}] Can't get the file: no file.", p_uriPath.ToString()));
							throw new Exception(String.Format("Unable to retrieve file {0}.", p_uriPath.ToString()));
						}
						Uri[] uriFilesToDownload = m_mrpModRepository.GetFilePartUrls(nxuModUrl.ModId, mfiFile.Id.ToString());
						string strSourcePath = Path.Combine(m_gmdGameMode.GameModeEnvironmentInfo.ModDownloadCacheDirectory, mfiFile.Filename);
						amdDescriptor = new AddModDescriptor(p_uriPath, strSourcePath, uriFilesToDownload, TaskStatus.Running);
						break;
					default:
						Trace.TraceInformation(String.Format("[{0}] Can't get the file.", p_uriPath.ToString()));
						throw new Exception("Unable to retrieve file: " + p_uriPath.ToString());
				}
				dicQueuedMods[p_uriPath.ToString()] = amdDescriptor;
				m_eifEnvironmentInfo.Settings.Save();
			}
			return amdDescriptor;
		}

		#region Mod Files Download

		/// <summary>
		/// Downloads the given files.
		/// </summary>
		/// <param name="p_lstFiles">The files to download.</param>
		protected void DownloadFiles(List<Uri> p_lstFiles)
		{
			Trace.TraceInformation(String.Format("[{0}] Downloading Files.", Descriptor.SourceUri.ToString()));
			foreach (Uri uriFile in p_lstFiles)
			{
				Trace.TraceInformation(String.Format("[{0}] Launching downloading of {1}.", Descriptor.SourceUri.ToString(), uriFile.ToString()));
				Dictionary<string, string> dicAuthenticationTokens = m_eifEnvironmentInfo.Settings.RepositoryAuthenticationTokens[m_mrpModRepository.Id];
				//TODO get the max connection and block size from settings
				FileDownloadTask fdtDownloader = new FileDownloadTask(4, 1024 * 500);
				fdtDownloader.TaskEnded += new EventHandler<TaskEndedEventArgs>(Downloader_TaskEnded);
				fdtDownloader.PropertyChanged += new PropertyChangedEventHandler(Downloader_PropertyChanged);
				fdtDownloader.DownloadAsync(uriFile, dicAuthenticationTokens, Path.GetDirectoryName(Descriptor.DefaultSourcePath), true);
				m_lstRunningTasks.Add(fdtDownloader);
			}
		}

		/// <summary>
		/// Handles the <see cref="INotifyPropertyChanged.PropertyChanged"/> event of the file downloader tasks.
		/// </summary>
		/// <param name="sender">The object that raised the event.</param>
		/// <param name="e">A <see cref="PropertyChangedEventArgs"/> describing the event arguments.</param>
		private void Downloader_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (m_booFinishedDownloads)
				return;
			if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.OverallProgress)))
			{
				Int32 intLastProgress = 0;
				if (m_dicLastProgress.ContainsKey((IBackgroundTask)sender))
					intLastProgress = m_dicLastProgress[(IBackgroundTask)sender];
				else if (m_dicLastProgress.Count == 0)
				{
					//this means this is the first update, or the first update after
					// the task was resumed, so the current item progress can be assumed
					// to be the last progress
					intLastProgress = ItemProgress;
				}
				if (intLastProgress < ((IBackgroundTask)sender).OverallProgress)
					ItemProgress += ((IBackgroundTask)sender).OverallProgress - intLastProgress;
				m_dicLastProgress[(IBackgroundTask)sender] = ((IBackgroundTask)sender).OverallProgress;
			}
			else if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.OverallProgressMaximum)))
			{
				ItemProgressMaximum += ((IBackgroundTask)sender).OverallProgressMaximum;
			}
		}

		/// <summary>
		/// Handles the <see cref="IBackgroundTask.TaskEnded"/> event of the file downloader tasks.
		/// </summary>
		/// <param name="sender">The object that raised the event.</param>
		/// <param name="e">A <see cref="TaskEndedEventArgs"/> describing the event arguments.</param>
		private void Downloader_TaskEnded(object sender, TaskEndedEventArgs e)
		{
			m_lstRunningTasks.Remove((IBackgroundTask)sender);
			if (e.Status == TaskStatus.Complete)
			{
				DownloadedFileInfo dfiDownloadInfo = (DownloadedFileInfo)e.ReturnValue;
				if (String.IsNullOrEmpty(Descriptor.SourcePath) && (Descriptor.DownloadFiles.IndexOf(dfiDownloadInfo.URL) == 0))
					Descriptor.SourcePath = dfiDownloadInfo.SavedFilePath;
				Descriptor.DownloadFiles.Remove(dfiDownloadInfo.URL);
				Descriptor.DownloadedFiles.Add(dfiDownloadInfo.SavedFilePath);

				KeyedSettings<AddModDescriptor> dicQueuedMods = m_eifEnvironmentInfo.Settings.QueuedModsToAdd[m_gmdGameMode.ModeId];
				dicQueuedMods[Descriptor.SourceUri.ToString()] = Descriptor;
				m_eifEnvironmentInfo.Settings.Save();

				if (Descriptor.DownloadFiles.Count == 0)
				{
					StepOverallProgress();
					AddModFile(m_cocConfirmOverwrite);
				}
			}
			else if (IsActive)
			{
				Status = e.Status;
				OnTaskEnded(e.Message, e.ReturnValue);
			}
		}

		#endregion

		#region Mod Addition

		/// <summary>
		/// Adds the mod file to the mod manager.
		/// </summary>
		/// <param name="p_cocConfirmOverwrite">The delegate to call to resolve conflicts with existing files.</param>
		protected void AddModFile(ConfirmOverwriteCallback p_cocConfirmOverwrite)
		{
			string strPath = String.IsNullOrEmpty(Descriptor.SourcePath) ? Descriptor.DefaultSourcePath : Descriptor.SourcePath;

			m_booFinishedDownloads = true;
			if (!File.Exists(strPath))
			{
				OverallMessage = String.Format("File does not exist: {0}", strPath);
				ItemMessage = "File does not exist";
				Status = TaskStatus.Error;
				OnTaskEnded(OverallMessage, null);
			}
			else
			{
				ModBuilder mbrModBuilder = new ModBuilder(m_gmdGameMode.GameModeEnvironmentInfo, m_eifEnvironmentInfo, new NexusFileUtil(m_eifEnvironmentInfo));
				mbrModBuilder.PropertyChanged += new PropertyChangedEventHandler(ModBuilder_PropertyChanged);
				mbrModBuilder.TaskEnded += new EventHandler<TaskEndedEventArgs>(ModBuilder_TaskEnded);
				mbrModBuilder.BuildFromFile(m_mfrModFormatRegistry, strPath, p_cocConfirmOverwrite);
				m_lstRunningTasks.Add(mbrModBuilder);
			}
		}

		/// <summary>
		/// Handles the <see cref="INotifyPropertyChanged.PropertyChanged"/> event of the mod builder task.
		/// </summary>
		/// <param name="sender">The object that raised the event.</param>
		/// <param name="e">A <see cref="PropertyChangedEventArgs"/> describing the event arguments.</param>
		private void ModBuilder_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.OverallMessage)))
				OverallMessage = ((IBackgroundTask)sender).OverallMessage;
			else if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.OverallProgress)))
			{
				if (OverallProgress - m_intOverallProgressOffset < ((IBackgroundTask)sender).OverallProgress)
					OverallProgress = ((IBackgroundTask)sender).OverallProgress + m_intOverallProgressOffset;
			}
			else if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.ItemMessage)))
				ItemMessage = ((IBackgroundTask)sender).ItemMessage;
			else if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.ItemProgress)))
			{
				if (ItemProgress < ((IBackgroundTask)sender).ItemProgress)
					ItemProgress = ((IBackgroundTask)sender).ItemProgress;
			}
			else if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.ItemProgressMaximum)))
			{
				ItemProgressMaximum = ((IBackgroundTask)sender).ItemProgressMaximum;
				ItemProgress = 0;
			}
			else if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.ItemProgressMinimum)))
			{
				ItemProgressMinimum = ((IBackgroundTask)sender).ItemProgressMinimum;
				ItemProgress = 0;
			}
		}

		/// <summary>
		/// Handles the <see cref="IBackgroundTask.TaskEnded"/> event of the mod builder task.
		/// </summary>
		/// <param name="sender">The object that raised the event.</param>
		/// <param name="e">A <see cref="TaskEndedEventArgs"/> describing the event arguments.</param>
		private void ModBuilder_TaskEnded(object sender, TaskEndedEventArgs e)
		{
			m_lstRunningTasks.Remove((IBackgroundTask)sender);
			if (Status == TaskStatus.Running)
			{
				if (e.Status == TaskStatus.Complete)
				{
					OverallMessage = String.Format("{0} has been added", GetModDisplayName());
					ItemMessage = "Finished copying";
					Status = TaskStatus.Complete;
					OnTaskEnded(e.ReturnValue);
				}
				else
				{
					OverallMessage = String.Format("{0} can't be added.", GetModDisplayName());
					ItemMessage = e.Message;
					//if we errored while adding, let's set to imcomplete so as to not
					// loose the file we download - the user may wish to do somethinng manual
					Status = (e.Status == TaskStatus.Error) ? TaskStatus.Incomplete : e.Status;
					OnTaskEnded(e.Message, e.ReturnValue);
				}
			}
		}

		#endregion

		#region Task Control

		/// <summary>
		/// Cancels the task.
		/// </summary>
		public override void Cancel()
		{
			base.Cancel();
			foreach (IBackgroundTask tskTask in m_lstRunningTasks)
				if ((tskTask.Status == TaskStatus.Running) || (tskTask.Status == TaskStatus.Paused) || (tskTask.Status == TaskStatus.Incomplete))
					tskTask.Cancel();
			OverallMessage = String.Format("Cancelled {0}", (ModInfo == null) ? m_uriPath.ToString() : GetModDisplayName());
			ItemMessage = "Cancelled";
			Status = TaskStatus.Cancelled;
			OnTaskEnded(Descriptor.SourceUri);
		}

		/// <summary>
		/// Pauses the task.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if the task does not support pausing.</exception>
		public override void Pause()
		{
			Status = TaskStatus.Paused;
			foreach (IBackgroundTask tskTask in m_lstRunningTasks)
				if (tskTask.SupportsPause)
					tskTask.Pause();
				else
					tskTask.Cancel();
			OnTaskEnded(Descriptor.SourceUri);
		}

		/// <summary>
		/// Resumes the task.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if the task is not paused.</exception>
		public override void Resume()
		{
			if ((Status != TaskStatus.Paused) && (Status != TaskStatus.Incomplete))
				throw new InvalidOperationException("Task is not paused.");
			m_lstRunningTasks.Clear();
			m_dicLastProgress.Clear();
			AddMod();
		}

		#endregion

		/// <summary>
		/// Raises the <see cref="INotifyPropertyChanged.PropertyChanged"/> event.
		/// </summary>
		/// <remarks>
		/// This persists the task state to storage, so it can be resumed on client restart.
		/// </remarks>
		/// <param name="e">A <see cref="PropertyChangedEventArgs"/> describing the event's arguments.</param>
		protected override void OnPropertyChanged(PropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);
			if (e.PropertyName.Equals(ObjectHelper.GetPropertyName<IBackgroundTask>(x => x.Status)))
			{
				Descriptor.Status = Status;
				KeyedSettings<AddModDescriptor> dicQueuedMods = m_eifEnvironmentInfo.Settings.QueuedModsToAdd[m_gmdGameMode.ModeId];
				dicQueuedMods[Descriptor.SourceUri.ToString()] = Descriptor;
				m_eifEnvironmentInfo.Settings.Save();
			}
		}

		/// <summary>
		/// Raises the <see cref="IBackgroundTask.TaskEnded"/> event.
		/// </summary>
		/// <remarks>
		/// This removes the task state from storage if it failed.
		/// </remarks>
		/// <param name="e">A <see cref="TaskEndedEventArgs"/> describing the event's arguments.</param>
		protected override void OnTaskEnded(TaskEndedEventArgs e)
		{
			base.OnTaskEnded(e);
			if ((e.Status != TaskStatus.Paused) && (e.Status != TaskStatus.Incomplete))
			{
				foreach (string strFile in Descriptor.DownloadedFiles)
					if (strFile.StartsWith(m_gmdGameMode.GameModeEnvironmentInfo.ModDownloadCacheDirectory, StringComparison.OrdinalIgnoreCase))
						FileUtil.ForceDelete(strFile);
				KeyedSettings<AddModDescriptor> dicQueuedMods = m_eifEnvironmentInfo.Settings.QueuedModsToAdd[m_gmdGameMode.ModeId];
				dicQueuedMods.Remove(Descriptor.SourceUri.ToString());
				m_eifEnvironmentInfo.Settings.Save();
			}
		}

		#region IDisposable Members

		/// <summary>
		/// Terminates all tasks started by this task.
		/// </summary>
		/// <remarks>
		/// After being disposed, that is no guarantee that the task's status will be correct. Further
		/// interaction with the object is undefined.
		/// </remarks>
		public void Dispose()
		{
			foreach (IDisposable tskTask in m_lstRunningTasks)
				tskTask.Dispose();
		}

		#endregion
	}
}