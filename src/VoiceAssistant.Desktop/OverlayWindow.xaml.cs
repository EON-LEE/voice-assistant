using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class OverlayWindow : Window
{
    private bool closingAfterCleanup;
    public OverlayWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(Height, MaxHeight);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Closing += (_, e) =>
        {
            if (closingAfterCleanup || ExitRequested is null) return;
            e.Cancel = true;
            ExitRequested.Invoke();
        };
    }

    public event Action? SettingsRequested;
    public event Action? StartRequested;
    public event Action? PauseRequested;
    public event Action? RetryRequested;
    public event Action? TranslationRequested;
    public event Action? StopRequested;
    public event Action? ExitRequested;
    public void CloseAfterCleanup()
    {
        closingAfterCleanup = true;
        Close();
    }

    public void ShowSessionState(bool capturing, string grounding, bool topmost, bool demo = false, bool paused = false)
    {
        CaptureStatus.Text = demo ? "OFFLINE DEMO · 고정 예시 · 마이크 / 네트워크 없음"
            : capturing && paused ? "마이크 일시정지 · 오디오 전송 중지"
            : capturing ? "● 듣는 중 · 영어 · 이전 대화 맥락 연결" : "마이크 꺼짐 · 우클릭으로 설정 및 시작";
        CaptureStatus.Foreground = demo || capturing && paused ? Brushes.Gold
            : capturing ? Brushes.LightGreen : Brushes.LightGray;
        CaptureDot.Fill = CaptureStatus.Foreground;
        GroundingText.Text = grounding;
        Topmost = topmost;
    }

    public void ShowConversation(IReadOnlyList<TranscriptTurn> turns, IReadOnlyDictionary<string, string> translations,
        bool demo)
    {
        ConversationPanel.Children.Clear();
        if (turns.Count == 0)
        {
            QuestionText.Text = "영어 대화를 기다리고 있어요.";
            ConversationPanel.Children.Add(QuestionText);
            return;
        }
        var recent = turns.TakeLast(3).ToArray();
        foreach (var turn in recent)
        {
            bool current = turn == recent[^1];
            var bubble = new StackPanel();
            bubble.Children.Add(new TextBlock
            {
                Text = turn.Text, TextWrapping = TextWrapping.Wrap, FontSize = current ? 15 : 13,
                LineHeight = current ? 25 : 21,
                Foreground = new SolidColorBrush(current ? Color.FromRgb(237, 242, 250) : Color.FromRgb(154, 170, 193))
            });
            bubble.Children.Add(new TextBlock
            {
                Text = !turn.IsFinal ? "듣는 중…" :
                    translations.TryGetValue(turn.TurnId, out var translation) ? translation :
                    demo ? "오프라인 예시 · 실제 번역 요청 없음" : "한국어 번역 중…",
                TextWrapping = TextWrapping.Wrap, FontSize = current ? 13 : 12,
                LineHeight = current ? 22 : 20,
                Foreground = new SolidColorBrush(current ? Color.FromRgb(180, 195, 216) : Color.FromRgb(137, 155, 181)),
                Margin = new Thickness(0, 5, 0, 0)
            });
            ConversationPanel.Children.Add(new Border
            {
                Background = current ? new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)) : Brushes.Transparent,
                BorderBrush = new SolidColorBrush(current ? Color.FromRgb(112, 136, 170) : Color.FromRgb(71, 88, 111)),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(9),
                Padding = current ? new Thickness(13, 10, 13, 10) : new Thickness(13, 0, 13, 0),
                Margin = new Thickness(0, 0, 0, 10),
                Child = bubble
            });
        }
        ConversationScroll.ScrollToEnd();
    }

    public void ShowSuggestions(ReplySnapshot? reply, IReadOnlyList<string> translations,
        string latestTurnId, string question, bool generating, string error, string context)
    {
        var answers = reply?.Answers ?? [];
        AnswerText.Text = answers.Count == 0 ? "대화가 인식되면 추천 답변이 여기에 표시됩니다." : answers[0];
        KoreanText.Text = translations.Count > 0 ? translations[0] : reply is null
            ? "영어 추천 답변의 한국어 뜻이 함께 표시됩니다." : "한국어 번역 준비 중…";
        AlternativeCard.Visibility = answers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AlternativeText.Text = answers.Count > 1 ? answers[1] : "";
        AlternativeKoreanText.Text = translations.Count > 1 ? translations[1] : "한국어 번역 준비 중…";
        bool previous = reply is not null && (reply.TurnId != latestTurnId || generating && reply.Complete);
        PrimaryLabel.Text = previous ? "01 · 이전 질문용 답변" : "01 · 바로 답하기";
        ContinuityText.Text = reply is null
            ? generating ? "내 문서에서 근거를 찾아 답변 준비 중…" : "우클릭으로 설정 / 라이브 시작"
            : $"{(previous ? "이전" : "현재")} 답변 기준: {question}" +
              (generating ? "\n새 추천 답변 생성 중 · 기존 답변 유지" : "\n추천은 실제로 말한 내용이 아닙니다.");
        ContextText.Text = context;
        SessionError.Text = error;
        SessionError.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        GroundingBadge.Text = reply?.Grounding switch
        {
            "grounded" => "맥락 + 내 문서 근거",
            "no_matches" => "문서 근거 없음",
            "unavailable" => "문서 검색 실패",
            "disabled" => "대화 맥락",
            _ => generating ? "근거 확인 중" : "맥락 연결"
        };
        SourcesText.Text = reply is null ? "" : string.Join(" · ", reply.Sources.Take(3).Select(source => source.Title)) +
            (reply.Sources.Count > 3 ? $" · 외 {reply.Sources.Count - 3}개" : "");
    }

    public void SetSessionControls(bool active, bool ready, bool paused, bool canTranslate)
    {
        StartMenu.IsEnabled = !active;
        StopMenu.IsEnabled = active;
        PauseMenu.IsEnabled = RetryMenu.IsEnabled = ready;
        TranslationMenu.IsEnabled = canTranslate;
        PauseMenu.Header = paused ? "다시 듣기" : "내가 말할 동안 일시정지";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Start_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke();
    private void Pause_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private void Retry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();
    private void Translation_Click(object sender, RoutedEventArgs e) => TranslationRequested?.Invoke();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void DragPanel(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
