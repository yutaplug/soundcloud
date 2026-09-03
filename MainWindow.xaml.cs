using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoundCloudDesktop.Models;
using SoundCloudDesktop.Services;

namespace SoundCloudDesktop;

public partial class MainWindow : Window
{
    private readonly SoundCloudApi _api = new();
    private readonly TokenStore _tokenStore = new();
    private readonly ObservableCollection<Track> _tracks = new();
    private readonly ICollectionView _trackView;
    private readonly ObservableCollection<Playlist> _playlists = new();
    private readonly ICollectionView _playlistView;
    private readonly ObservableCollection<Track> _playlistTracks = new();
    private readonly ICollectionView _playlistTrackView;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly Random _random = new();
    private readonly List<int> _shuffleOrder = new();
    private Track? _currentTrack;
    private int _currentIndex = -1;
    private bool _isLoading;
    private bool _repeat;
    private bool _shuffle;
    private bool _isSeeking;
    private bool _playWhenOpened;
    private string? _currentMediaFile;
    private int _shufflePosition = -1;
    private bool _showPlaylists;
    private bool _showPlaylistTracks;
    private Playlist? _currentPlaylist;

    public MainWindow()
    {
        InitializeComponent();
        _trackView = CollectionViewSource.GetDefaultView(_tracks);
        _trackView.Filter = TrackMatchesSearch;
        _playlistView = CollectionViewSource.GetDefaultView(_playlists);
        _playlistView.Filter = PlaylistMatchesSearch;
        _playlistTrackView = CollectionViewSource.GetDefaultView(_playlistTracks);
        _playlistTrackView.Filter = TrackMatchesSearch;
        TrackList.ItemsSource = _trackView;
        PlaylistList.ItemsSource = _playlistView;
        _player.Volume = VolumeSlider.Value;
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _positionTimer.Tick += PositionTimer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        var savedToken = _tokenStore.TryLoad();
        if (!string.IsNullOrWhiteSpace(savedToken))
        {
            TokenBox.Password = savedToken;
            await LoginWithTokenAsync(savedToken);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await LoginWithTokenAsync(TokenBox.Password.Trim());
    }

    private async Task LoginWithTokenAsync(string token)
    {
        if (_isLoading) return;
        if (string.IsNullOrWhiteSpace(token))
        {
            LoginStatus.Text = "Enter an oauth_token to continue.";
            return;
        }

        SetBusy(true, "Connecting to SoundCloud…");
        try
        {
            var profile = await _api.GetCurrentUserAsync(token);
            _tokenStore.TrySave(token);
            ProfileName.Text = profile.UserName;
            LoginView.Visibility = Visibility.Collapsed;
            AppView.Visibility = Visibility.Visible;
            SetBusy(false);
            await LoadLibraryAsync();
        }
        catch (Exception ex)
        {
            LoginStatus.Text = FriendlyError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadTracksAsync()
    {
        if (_isLoading) return;
        SetBusy(true, "Loading all liked tracks…");
        try
        {
            _tracks.Clear();
            var loaded = await _api.GetLikedTracksAsync();
            var unique = new HashSet<long>();
            foreach (var track in loaded)
                if ((track.Id <= 0 || unique.Add(track.Id)) && track.Title.Length > 0) _tracks.Add(track);

            UpdateTrackSummary();
        }
        catch (Exception ex)
        {
            PageStatus.Text = FriendlyError(ex);
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadLibraryAsync()
    {
        await LoadTracksAsync();
        await LoadPlaylistsAsync();
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_isLoading) return;
        SetBusy(true, "Loading liked playlists…");
        try
        {
            _playlists.Clear();
            var loaded = await _api.GetLikedPlaylistsAsync();
            var unique = new HashSet<long>();
            foreach (var playlist in loaded)
                if ((playlist.Id <= 0 || unique.Add(playlist.Id)) && playlist.Title.Length > 0) _playlists.Add(playlist);
            UpdatePlaylistSummary();
        }
        catch (Exception ex)
        {
            PageStatus.Text = FriendlyError(ex);
            PlaylistEmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadLibraryAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _trackView.Refresh();
        _playlistView.Refresh();
        _playlistTrackView.Refresh();
        UpdateVisibleSummary();
    }

    private void LikedTracks_Click(object sender, RoutedEventArgs e) => ShowTracks();

    private void LikedPlaylists_Click(object sender, RoutedEventArgs e) => ShowPlaylists();

    private async void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistList.SelectedItem is Playlist playlist) await OpenPlaylistAsync(playlist);
    }

    private void BackToPlaylists_Click(object sender, RoutedEventArgs e) => ShowPlaylists();

    private void ShowTracks()
    {
        _showPlaylists = false;
        _showPlaylistTracks = false;
        _currentPlaylist = null;
        SectionTitle.Text = " / Liked tracks";
        PageTitle.Text = "Liked tracks";
        TrackList.ItemsSource = _trackView;
        TracksContent.Visibility = Visibility.Visible;
        PlaylistsContent.Visibility = Visibility.Collapsed;
        PlayAllButton.Visibility = Visibility.Visible;
        BackToPlaylistsButton.Visibility = Visibility.Collapsed;
        LikedTracksButton.Background = (Brush)FindResource("PanelLightBrush");
        LikedPlaylistsButton.Background = Brushes.Transparent;
        UpdateTrackSummary();
    }

    private void ShowPlaylists()
    {
        _showPlaylists = true;
        _showPlaylistTracks = false;
        _currentPlaylist = null;
        SectionTitle.Text = " / Liked playlists";
        PageTitle.Text = "Liked playlists";
        TracksContent.Visibility = Visibility.Collapsed;
        PlaylistsContent.Visibility = Visibility.Visible;
        PlayAllButton.Visibility = Visibility.Collapsed;
        BackToPlaylistsButton.Visibility = Visibility.Collapsed;
        LikedTracksButton.Background = Brushes.Transparent;
        LikedPlaylistsButton.Background = (Brush)FindResource("PanelLightBrush");
        UpdatePlaylistSummary();
    }

    private async Task OpenPlaylistAsync(Playlist playlist)
    {
        if (_isLoading) return;
        SetBusy(true, $"Loading “{playlist.Title}”…");
        try
        {
            var loaded = await _api.GetPlaylistTracksAsync(playlist);
            _playlistTracks.Clear();
            foreach (var track in loaded) _playlistTracks.Add(track);
            _currentPlaylist = playlist;
            _showPlaylists = false;
            _showPlaylistTracks = true;
            SectionTitle.Text = $" / {playlist.Title}";
            PageTitle.Text = playlist.Title;
            TrackList.ItemsSource = _playlistTrackView;
            TracksContent.Visibility = Visibility.Visible;
            PlaylistsContent.Visibility = Visibility.Collapsed;
            PlayAllButton.Visibility = Visibility.Visible;
            BackToPlaylistsButton.Visibility = Visibility.Visible;
            _shuffleOrder.Clear();
            _shufflePosition = -1;
            if (_shuffle) BuildShuffleOrder();
            UpdatePlaylistTrackSummary();
        }
        catch (Exception ex)
        {
            PageStatus.Text = FriendlyError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _positionTimer.Stop();
        _tracks.Clear();
        _playlists.Clear();
        _playlistTracks.Clear();
        _tokenStore.Delete();
        _currentTrack = null;
        _currentIndex = -1;
        NowPlayingTitle.Text = "Nothing playing";
        NowPlayingArtist.Text = "";
        NowPlayingArtwork.Source = null;
        TokenBox.Clear();
        LoginStatus.Text = "";
        AppView.Visibility = Visibility.Collapsed;
        LoginView.Visibility = Visibility.Visible;
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private async void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is Track track) await PlayTrackAsync(track);
    }

    private async void TrackPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Track track }) await PlayTrackAsync(track);
    }

    private async void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        var track = (_showPlaylistTracks ? _playlistTrackView : _trackView).Cast<Track>().FirstOrDefault();
        if (track is not null) await PlayTrackAsync(track);
    }

    private async Task PlayTrackAsync(Track track)
    {
        var queue = CurrentQueue;
        var index = queue.IndexOf(track);
        if (index >= 0)
        {
            _currentIndex = index;
            SyncShufflePosition();
        }
        _currentTrack = track;
        TrackList.SelectedItem = track;
        NowPlayingTitle.Text = track.Title;
        NowPlayingArtist.Text = track.Artist;
        NowPlayingArtwork.Source = ImageSourceFromUrl(track.ArtworkUrl);
        ElapsedText.Text = "0:00";
        TotalText.Text = track.DurationText;
        ProgressSlider.Value = 0;
        PlayPauseButton.Content = "Ⅱ";
        PageStatus.Text = $"Preparing “{track.Title}”…";

        try
        {
            SetBusy(true, $"Buffering “{track.Title}”…");
            var url = await _api.GetPlayableUrlAsync(track);
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("This track is not available for streaming.");
            var localFile = await _api.DownloadStreamToTempFileAsync(url);
            _player.Stop();
            DeleteCurrentMediaFile();
            _currentMediaFile = localFile;
            _playWhenOpened = true;
            _player.Open(new Uri(localFile, UriKind.Absolute));
            _positionTimer.Start();
            PageStatus.Text = "Now playing";
        }
        catch (Exception ex)
        {
            PlayPauseButton.Content = "▶";
            PageStatus.Text = FriendlyError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is null)
        {
            if (CurrentQueue.Count > 0) _ = PlayTrackAsync(CurrentQueue[0]);
            return;
        }
        if (PlayPauseButton.Content?.ToString() == "Ⅱ")
        {
            _playWhenOpened = false;
            _player.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            _player.Play();
            PlayPauseButton.Content = "Ⅱ";
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentQueue.Count == 0) return;
        var index = _shuffle ? GetPreviousShuffleIndex() : (_currentIndex <= 0 ? CurrentQueue.Count - 1 : _currentIndex - 1);
        await PlayTrackAsync(CurrentQueue[index]);
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await PlayNextAsync();

    private async Task PlayNextAsync()
    {
        if (CurrentQueue.Count == 0) return;
        var index = _shuffle ? GetNextShuffleIndex() : (_currentIndex + 1) % CurrentQueue.Count;
        await PlayTrackAsync(CurrentQueue[index]);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        if (_shuffle) BuildShuffleOrder();
        else _shuffleOrder.Clear();
        SetToggleVisual(ShuffleButton, _shuffle);
        PageStatus.Text = _shuffle ? "Shuffle on" : "Shuffle off";
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _repeat = !_repeat;
        SetToggleVisual(RepeatButton, _repeat);
        PageStatus.Text = _repeat ? "Repeat on" : "Repeat off";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = VolumeSlider.Value;
    }

    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var step = e.Delta > 0 ? 0.05 : -0.05;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, VolumeSlider.Minimum, VolumeSlider.Maximum);
        e.Handled = true;
    }

    private void ProgressSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_player.NaturalDuration.HasTimeSpan && ProgressSlider.Maximum > 0)
        {
            _player.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
            _isSeeking = false;
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_playWhenOpened)
            {
                _player.Play();
                PlayPauseButton.Content = "Ⅱ";
            }
            if (_player.NaturalDuration.HasTimeSpan)
            {
                var total = _player.NaturalDuration.TimeSpan;
                ProgressSlider.Maximum = Math.Max(1, total.TotalSeconds);
                TotalText.Text = FormatTime(total);
            }
        });
    }

    private async void Player_MediaEnded(object? sender, EventArgs e)
    {
        if (_repeat)
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
            return;
        }

        await Dispatcher.InvokeAsync(async () =>
        {
            await PlayNextAsync();
        });
    }

    private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _playWhenOpened = false;
            PlayPauseButton.Content = "▶";
            var detail = e.ErrorException?.Message;
            PageStatus.Text = string.IsNullOrWhiteSpace(detail)
                ? "This track could not be played by Windows Media Player."
                : $"Playback failed: {detail}";
        });
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isSeeking || !_player.NaturalDuration.HasTimeSpan) return;
        var position = _player.Position;
        ElapsedText.Text = FormatTime(position);
        if (ProgressSlider.Maximum > 0) ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, position.TotalSeconds);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isLoading = busy;
        BusyText.Text = message ?? "Loading…";
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.IsEnabled = !busy;
    }

    private static ImageSource? ImageSourceFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return new System.Windows.Media.Imaging.BitmapImage(uri);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException => ex.Message,
        TaskCanceledException => "SoundCloud took too long to respond.",
        _ => string.IsNullOrWhiteSpace(ex.Message) ? "Something went wrong." : ex.Message
    };

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private bool TrackMatchesSearch(object item)
    {
        if (item is not Track track) return false;
        var query = SearchBox?.Text.Trim() ?? "";
        return string.IsNullOrWhiteSpace(query) ||
               track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool PlaylistMatchesSearch(object item)
    {
        if (item is not Playlist playlist) return false;
        var query = SearchBox?.Text.Trim() ?? "";
        return string.IsNullOrWhiteSpace(query) ||
               playlist.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               playlist.Creator.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateVisibleSummary()
    {
        if (_showPlaylistTracks) UpdatePlaylistTrackSummary();
        else if (_showPlaylists) UpdatePlaylistSummary();
        else UpdateTrackSummary();
    }

    private IList<Track> CurrentQueue => _showPlaylistTracks ? _playlistTracks : _tracks;

    private void UpdateTrackSummary()
    {
        var visibleCount = _trackView.Cast<Track>().Count();
        TrackCount.Text = string.IsNullOrWhiteSpace(SearchBox?.Text)
            ? (_tracks.Count == 1 ? "1 track" : $"{_tracks.Count:N0} tracks")
            : $"{visibleCount:N0} match{(visibleCount == 1 ? "" : "es")}";
        PageStatus.Text = _tracks.Count == 0
            ? ""
            : string.IsNullOrWhiteSpace(SearchBox?.Text) ? "Double-click a track to play it." : "Showing liked tracks matching your search.";
        EmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlaylistSummary()
    {
        var visibleCount = _playlistView.Cast<Playlist>().Count();
        TrackCount.Text = string.IsNullOrWhiteSpace(SearchBox?.Text)
            ? (_playlists.Count == 1 ? "1 playlist" : $"{_playlists.Count:N0} playlists")
            : $"{visibleCount:N0} match{(visibleCount == 1 ? "" : "es")}";
        PageStatus.Text = _playlists.Count == 0
            ? ""
            : string.IsNullOrWhiteSpace(SearchBox?.Text) ? "Your liked playlists." : "Showing liked playlists matching your search.";
        PlaylistEmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlaylistTrackSummary()
    {
        var visibleCount = _playlistTrackView.Cast<Track>().Count();
        TrackCount.Text = string.IsNullOrWhiteSpace(SearchBox?.Text)
            ? (_playlistTracks.Count == 1 ? "1 track" : $"{_playlistTracks.Count:N0} tracks")
            : $"{visibleCount:N0} match{(visibleCount == 1 ? "" : "es")}";
        PageStatus.Text = _playlistTracks.Count == 0
            ? "This playlist has no visible tracks."
            : string.IsNullOrWhiteSpace(SearchBox?.Text) ? "Double-click a track to play it." : "Showing playlist tracks matching your search.";
        EmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetToggleVisual(Button button, bool enabled)
    {
        button.Background = enabled ? (Brush)FindResource("OrangeBrush") : Brushes.Transparent;
        button.Foreground = enabled ? Brushes.White : (Brush)FindResource("MutedBrush");
        button.BorderBrush = enabled ? (Brush)FindResource("OrangeBrush") : Brushes.Transparent;
        button.BorderThickness = enabled ? new Thickness(1) : new Thickness(0);
    }

    private void BuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        for (var i = 0; i < CurrentQueue.Count; i++) _shuffleOrder.Add(i);
        for (var i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }
        if (_currentIndex >= 0)
        {
            var currentPosition = _shuffleOrder.IndexOf(_currentIndex);
            if (currentPosition > 0) (_shuffleOrder[0], _shuffleOrder[currentPosition]) = (_shuffleOrder[currentPosition], _shuffleOrder[0]);
            _shufflePosition = 0;
        }
        SyncShufflePosition();
    }

    private void SyncShufflePosition()
    {
        if (!_shuffle || _currentIndex < 0) return;
        if (_shuffleOrder.Count != CurrentQueue.Count) BuildShuffleOrder();
        var position = _shuffleOrder.IndexOf(_currentIndex);
        if (position >= 0) _shufflePosition = position;
    }

    private int GetNextShuffleIndex()
    {
        if (_shuffleOrder.Count != CurrentQueue.Count) BuildShuffleOrder();
        var nextPosition = _shufflePosition < 0 ? 0 : _shufflePosition + 1;
        if (nextPosition >= _shuffleOrder.Count)
        {
            BuildShuffleOrder();
            nextPosition = _shuffleOrder.Count > 1 ? 1 : 0;
        }
        _shufflePosition = nextPosition;
        return _shuffleOrder[nextPosition];
    }

    private int GetPreviousShuffleIndex()
    {
        if (_shuffleOrder.Count != CurrentQueue.Count) BuildShuffleOrder();
        var previousPosition = _shufflePosition <= 0 ? _shuffleOrder.Count - 1 : _shufflePosition - 1;
        _shufflePosition = previousPosition;
        return _shuffleOrder[previousPosition];
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _positionTimer.Stop();
        _player.Close();
        DeleteCurrentMediaFile();
        _api.Dispose();
    }

    private void DeleteCurrentMediaFile()
    {
        if (string.IsNullOrWhiteSpace(_currentMediaFile)) return;
        try { if (File.Exists(_currentMediaFile)) File.Delete(_currentMediaFile); } catch { }
        _currentMediaFile = null;
    }
}
