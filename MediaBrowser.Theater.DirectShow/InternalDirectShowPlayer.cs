﻿using MediaBrowser.Common.Events;
using MediaBrowser.Model.ApiClient;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Theater.Interfaces.Configuration;
using MediaBrowser.Theater.Interfaces.Playback;
using MediaBrowser.Theater.Interfaces.Presentation;
using MediaBrowser.Theater.Interfaces.UserInput;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MediaBrowser.Theater.DirectShow
{
    public class InternalDirectShowPlayer : IInternalMediaPlayer
    {
        private DirectShowPlayer _mediaPlayer;

        private readonly ILogger _logger;
        private readonly IHiddenWindow _hiddenWindow;
        private readonly IPresentationManager _presentation;
        private readonly IUserInputManager _userInput;
        private readonly IApiClient _apiClient;
        private readonly IPlaybackManager _playbackManager;
        private readonly ITheaterConfigurationManager _config;
        private readonly IIsoManager _isoManager;

        private readonly Task _taskResult = Task.FromResult(true);

        public event EventHandler<MediaChangeEventArgs> MediaChanged;

        public event EventHandler<PlaybackStopEventArgs> PlaybackCompleted;

        private List<BaseItemDto> _playlist = new List<BaseItemDto>();

        public InternalDirectShowPlayer(ILogManager logManager, IHiddenWindow hiddenWindow, IPresentationManager presentation, IUserInputManager userInput, IApiClient apiClient, IPlaybackManager playbackManager, ITheaterConfigurationManager config, IIsoManager isoManager)
        {
            _logger = logManager.GetLogger("DirectShowPlayer");
            _hiddenWindow = hiddenWindow;
            _presentation = presentation;
            _userInput = userInput;
            _apiClient = apiClient;
            _playbackManager = playbackManager;
            _config = config;
            _isoManager = isoManager;
        }

        public IReadOnlyList<BaseItemDto> Playlist
        {
            get
            {
                return _playlist;
            }
        }

        public int CurrentPlaylistIndex { get; private set; }

        public PlayOptions CurrentPlayOptions { get; private set; }

        public BaseItemDto CurrentMedia
        {
            get
            {
                return _playlist.Count > 0 ? _playlist[CurrentPlaylistIndex] : null;
            }
        }

        public bool CanSeek
        {
            get { return true; }
        }

        public bool CanPause
        {
            get { return true; }
        }

        public bool CanQueue
        {
            get { return true; }
        }

        public bool CanTrackProgress
        {
            get { return true; }
        }

        public string Name
        {
            get { return "Internal Player"; }
        }

        public bool SupportsMultiFilePlayback
        {
            get { return true; }
        }

        public PlayState PlayState
        {
            get
            {
                if (_mediaPlayer == null)
                {
                    return PlayState.Idle;
                }

                return _mediaPlayer.PlayState;
            }
        }

        public long? CurrentPositionTicks
        {
            get
            {
                if (_mediaPlayer != null)
                {
                    return _mediaPlayer.CurrentPositionTicks;
                }

                return null;
            }
        }

        public bool CanPlayByDefault(BaseItemDto item)
        {
            return item.IsVideo || item.IsAudio;
        }

        public bool CanPlayMediaType(string mediaType)
        {
            return new[] { MediaType.Video, MediaType.Audio }.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
        }

        public async Task Play(PlayOptions options)
        {
            await _presentation.Window.Dispatcher.InvokeAsync(async () => await PlayInternal(options));
        }

        private async Task PlayInternal(PlayOptions options)
        {
            CurrentPlaylistIndex = 0;
            CurrentPlayOptions = options;

            _playlist = options.Items.ToList();

            try
            {
                _mediaPlayer = new DirectShowPlayer(_logger, _hiddenWindow, this)
                {
                    BackColor = Color.Black,
                    FormBorderStyle = FormBorderStyle.None,
                    TopLevel = false
                };

                _hiddenWindow.WindowsFormsHost.Child = _mediaPlayer;

                await PlayTrack(0, options.StartPositionTicks);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error beginning playback", ex);

                DisposePlayer();

                throw;
            }
        }

        private async Task PlayTrack(int index, long? startPositionTicks)
        {
            var previousMedia = CurrentMedia;
            var previousIndex = CurrentPlaylistIndex;
            var endingTicks = CurrentPositionTicks;

            var options = CurrentPlayOptions;

            var playableItem = await GetPlayableItem(options.Items[index], CancellationToken.None);

            try
            {
                _mediaPlayer.Play(playableItem, EnableReclock(options), EnableMadvr(options));
            }
            catch
            {
                DisposeMount(playableItem);

                throw;
            }

            CurrentPlaylistIndex = index;

            if (startPositionTicks.HasValue && startPositionTicks.Value > 0)
            {
                _mediaPlayer.Seek(startPositionTicks.Value);
            }

            if (previousMedia != null)
            {
                var args = new MediaChangeEventArgs
                {
                    Player = this,
                    NewPlaylistIndex = index,
                    NewMedia = CurrentMedia,
                    PreviousMedia = previousMedia,
                    PreviousPlaylistIndex = previousIndex,
                    EndingPositionTicks = endingTicks
                };

                EventHelper.FireEventIfNotNull(MediaChanged, this, args, _logger);
            }
        }

        private async Task<PlayableItem> GetPlayableItem(BaseItemDto item, CancellationToken cancellationToken)
        {
            IIsoMount mountedIso = null;

            if (item.VideoType.HasValue && item.VideoType.Value == VideoType.Iso && item.IsoType.HasValue && _isoManager.CanMount(item.Path))
            {
                try
                {
                    mountedIso = await _isoManager.Mount(item.Path, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error mounting iso {0}", ex, item.Path);
                }
            }

            return new PlayableItem
            {
                OriginalItem = item,
                PlayablePath = PlayablePathBuilder.GetPlayablePath(item, mountedIso, _apiClient)
            };
        }

        /// <summary>
        /// Enables the madvr.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise</returns>
        private bool EnableMadvr(PlayOptions options)
        {
            var video = options.Items.First();

            if (!video.IsVideo)
            {
                return false;
            }

            if (!_config.Configuration.InternalPlayerConfiguration.EnableMadvr)
            {
                return false;
            }

            if (!options.GoFullScreen)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Enables the madvr.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise</returns>
        private bool EnableReclock(PlayOptions options)
        {
            var video = options.Items.First();

            if (!video.IsVideo)
            {
                return false;
            }

            if (!_config.Configuration.InternalPlayerConfiguration.EnableReclock)
            {
                return false;
            }

            return true;
        }

        private void DisposePlayer()
        {
            if (_mediaPlayer != null)
            {
                _presentation.Window.Dispatcher.InvokeAsync(() =>
                {
                    _mediaPlayer.Dispose();
                    _hiddenWindow.WindowsFormsHost.Child = new Panel();

                });
            }
        }

        public void Pause()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Pause();
            }
        }

        public void UnPause()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Unpause();
            }
        }

        public void Stop()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop(TrackCompletionReason.Stop, null);
            }
        }

        public void Seek(long positionTicks)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Seek(positionTicks);
            }
        }

        /// <summary>
        /// Occurs when [play state changed].
        /// </summary>
        public event EventHandler PlayStateChanged;
        internal void OnPlayStateChanged()
        {
            EventHelper.FireEventIfNotNull(PlayStateChanged, this, EventArgs.Empty, _logger);
        }

        private void DisposeMount(PlayableItem media)
        {
            if (media.IsoMount != null)
            {
                try
                {
                    media.IsoMount.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error unmounting iso {0}", ex, media.IsoMount.MountedPath);
                }
            }
        }

        internal async void OnPlaybackStopped(PlayableItem media, long? positionTicks, TrackCompletionReason reason, int? newTrackIndex)
        {
            DisposeMount(media);

            if (reason == TrackCompletionReason.Ended || reason == TrackCompletionReason.ChangeTrack)
            {
                var nextIndex = newTrackIndex ?? (CurrentPlaylistIndex + 1);

                if (nextIndex < CurrentPlayOptions.Items.Count)
                {
                    await PlayTrack(nextIndex, null);
                    return;
                }
            }

            DisposePlayer();

            var args = new PlaybackStopEventArgs
            {
                Player = this,
                Playlist = _playlist,
                EndingMedia = media.OriginalItem,
                EndingPositionTicks = positionTicks

            };

            EventHelper.FireEventIfNotNull(PlaybackCompleted, this, args, _logger);

            _playbackManager.ReportPlaybackCompleted(args);
        }

        public void ChangeTrack(int newIndex)
        {
            _mediaPlayer.Stop(TrackCompletionReason.ChangeTrack, newIndex);
        }
    }

    public enum TrackCompletionReason
    {
        Stop,
        Ended,
        ChangeTrack
    }
}
