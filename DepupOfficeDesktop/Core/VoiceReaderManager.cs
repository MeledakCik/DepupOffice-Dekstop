using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting dari VoiceReaderManager.kt:
    /// 100% Offline Document Voice Reader (Text-To-Speech) memakai Windows SAPI 5 bawaan OS
    /// (tanpa paket NuGet eksternal). Dilengkapi dialog kontrol Play, Pause, Resume, Stop,
    /// pilihan kecepatan (0.75x, 1.0x, 1.25x, 1.5x, 2.0x), dan Bahasa (Indonesia & English).
    /// </summary>
    public sealed class VoiceReaderManager : IDisposable
    {
        private List<string> _paragraphs = new();
        private int _currentIndex = 0;
        private bool _isPlaying = false;
        private bool _isPaused = false;
        private float _speechRate = 1.0f;
        private string _langCode = "id-ID";
        private CancellationTokenSource? _cts;
        private dynamic? _activeSpVoice;
        private readonly object _lock = new();

        public event Action<int, int, string>? OnProgressUpdate;
        public event Action<bool, bool>? OnStateChange;

        public void StartReading(string text, string langCode = "id-ID")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Stop();
            _langCode = langCode;
            _paragraphs = text.Replace("\r\n", "\n")
                              .Split('\n')
                              .Select(p => p.Trim())
                              .Where(p => !string.IsNullOrWhiteSpace(p))
                              .ToList();
            if (_paragraphs.Count == 0)
                _paragraphs.Add(text.Trim());

            _currentIndex = 0;
            _isPaused = false;
            _isPlaying = true;
            RunSpeechLoop();
        }

        private void RunSpeechLoop()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                try
                {
                    Type? spType = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (spType == null)
                    {
                        OnStateChange?.Invoke(false, false);
                        return;
                    }
                    dynamic voice = Activator.CreateInstance(spType)!;
                    lock (_lock) { _activeSpVoice = voice; }

                    while (_currentIndex < _paragraphs.Count && !token.IsCancellationRequested)
                    {
                        string para = _paragraphs[_currentIndex];
                        OnProgressUpdate?.Invoke(_currentIndex + 1, _paragraphs.Count, para);
                        OnStateChange?.Invoke(true, false);

                        // Rate SAPI berkisar -10 s/d +10 (0 = 1.0x)
                        int sapiRate = _speechRate switch
                        {
                            <= 0.75f => -2,
                            <= 1.0f => 0,
                            <= 1.25f => 2,
                            <= 1.5f => 4,
                            _ => 6
                        };
                        try { voice.Rate = sapiRate; } catch { }
                        try
                        {
                            // SVSFlagsAsync = 1, PurgeBeforeSpeak = 2
                            voice.Speak(para, 1);
                            while (!voice.WaitUntilDone(100))
                            {
                                if (token.IsCancellationRequested)
                                {
                                    voice.Speak("", 2);
                                    return;
                                }
                            }
                        }
                        catch { break; }

                        if (token.IsCancellationRequested || _isPaused) return;
                        _currentIndex++;
                    }

                    _isPlaying = false;
                    _isPaused = false;
                    OnStateChange?.Invoke(false, false);
                }
                catch
                {
                    OnStateChange?.Invoke(false, false);
                }
            }, token);
        }

        public void Pause()
        {
            if (!_isPlaying) return;
            _isPaused = true;
            _isPlaying = false;
            _cts?.Cancel();
            lock (_lock)
            {
                try { _activeSpVoice?.Speak("", 2); } catch { }
            }
            OnStateChange?.Invoke(false, true);
        }

        public void Resume()
        {
            if (!_isPaused || _paragraphs.Count == 0) return;
            _isPaused = false;
            _isPlaying = true;
            RunSpeechLoop();
        }

        public void Stop()
        {
            _isPlaying = false;
            _isPaused = false;
            _cts?.Cancel();
            lock (_lock)
            {
                try { _activeSpVoice?.Speak("", 2); } catch { }
            }
            _currentIndex = 0;
            OnStateChange?.Invoke(false, false);
        }

        public void SetSpeechRate(float rate)
        {
            _speechRate = rate;
            lock (_lock)
            {
                if (_activeSpVoice != null)
                {
                    int sapiRate = rate switch
                    {
                        <= 0.75f => -2,
                        <= 1.0f => 0,
                        <= 1.25f => 2,
                        <= 1.5f => 4,
                        _ => 6
                    };
                    try { _activeSpVoice.Rate = sapiRate; } catch { }
                }
            }
        }

        public void SetLanguage(string langCode) => _langCode = langCode;

        public void Dispose() => Stop();

        public static void ShowVoiceReaderDialog(Form owner, string title, string contentText)
        {
            if (string.IsNullOrWhiteSpace(contentText))
            {
                Theme.Toast(owner, "Tidak ada teks yang dapat dibacakan");
                return;
            }

            var reader = new VoiceReaderManager();
            string selectedLang = "id-ID";
            float currentRate = 1.0f;
            bool isReading = false;
            bool isPaused = false;

            using var dlg = new Form
            {
                Text = $"Voice Reader — {title}",
                Size = new Size(520, 390),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22) };
            dlg.Controls.Add(root);

            var lblHeader = new Label
            {
                Text = $"🔊 Voice Reader — {title}",
                Font = Theme.FontHead(13f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(22, 18)
            };
            root.Controls.Add(lblHeader);

            var lblProgress = new Label
            {
                Text = "Siap membaca...",
                Font = Theme.FontBody(9.5f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(22, 48)
            };
            root.Controls.Add(lblProgress);

            var txtPreview = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.BG,
                ForeColor = ColorTranslator.FromHtml("#334155"),
                Font = Theme.FontBody(10f),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(22, 74),
                Size = new Size(460, 100),
                Text = contentText.Length > 220 ? contentText.Substring(0, 220) + "..." : contentText
            };
            root.Controls.Add(txtPreview);

            // Speed Selector Bar
            var lblSpeed = new Label
            {
                Text = "Kecepatan:",
                Font = Theme.FontLabel(9.5f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(22, 192)
            };
            root.Controls.Add(lblSpeed);

            float[] rates = { 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
            var rateButtons = new List<Button>();
            int rx = 110;
            foreach (float r in rates)
            {
                float rateVal = r;
                var b = new Button
                {
                    Text = $"{rateVal:0.##}x",
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.FontLabel(9f),
                    Size = new Size(64, 30),
                    Location = new Point(rx, 186),
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) =>
                {
                    currentRate = rateVal;
                    reader.SetSpeechRate(currentRate);
                    UpdateRateButtons();
                };
                rateButtons.Add(b);
                root.Controls.Add(b);
                rx += 70;
            }

            void UpdateRateButtons()
            {
                for (int i = 0; i < rates.Length; i++)
                {
                    bool active = Math.Abs(rates[i] - currentRate) < 0.01f;
                    rateButtons[i].BackColor = active ? Theme.ACCENT : Theme.SURFACE2;
                    rateButtons[i].ForeColor = active ? Color.White : Theme.TEXT;
                }
            }
            UpdateRateButtons();

            // Language Selector Bar
            var lblLang = new Label
            {
                Text = "Bahasa:",
                Font = Theme.FontLabel(9.5f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(22, 234)
            };
            root.Controls.Add(lblLang);

            var btnId = new Button
            {
                Text = "🇮🇩 Indonesia",
                FlatStyle = FlatStyle.Flat,
                Font = Theme.FontLabel(9.5f),
                Size = new Size(130, 32),
                Location = new Point(110, 228),
                Cursor = Cursors.Hand
            };
            btnId.FlatAppearance.BorderSize = 0;

            var btnEn = new Button
            {
                Text = "🇬🇧 English",
                FlatStyle = FlatStyle.Flat,
                Font = Theme.FontLabel(9.5f),
                Size = new Size(130, 32),
                Location = new Point(248, 228),
                Cursor = Cursors.Hand
            };
            btnEn.FlatAppearance.BorderSize = 0;

            void UpdateLangButtons(bool isId)
            {
                Color green = ColorTranslator.FromHtml("#16A34A");
                btnId.BackColor = isId ? green : Theme.SURFACE2;
                btnId.ForeColor = isId ? Color.White : Theme.TEXT;
                btnEn.BackColor = !isId ? green : Theme.SURFACE2;
                btnEn.ForeColor = !isId ? Color.White : Theme.TEXT;
            }
            btnId.Click += (s, e) => { selectedLang = "id-ID"; reader.SetLanguage(selectedLang); UpdateLangButtons(true); };
            btnEn.Click += (s, e) => { selectedLang = "en-US"; reader.SetLanguage(selectedLang); UpdateLangButtons(false); };
            UpdateLangButtons(true);
            root.Controls.Add(btnId);
            root.Controls.Add(btnEn);

            // Play / Pause / Stop Buttons
            var btnPlay = new Button
            {
                Text = "▶️ Putar Suara",
                FlatStyle = FlatStyle.Flat,
                Font = Theme.FontLabel(10.5f),
                BackColor = Theme.ACCENT,
                ForeColor = Color.White,
                Size = new Size(170, 42),
                Location = new Point(95, 282),
                Cursor = Cursors.Hand
            };
            btnPlay.FlatAppearance.BorderSize = 0;

            var btnStop = new Button
            {
                Text = "⏹️ Stop",
                FlatStyle = FlatStyle.Flat,
                Font = Theme.FontLabel(10.5f),
                BackColor = Theme.LINE,
                ForeColor = Theme.TEXT,
                Size = new Size(120, 42),
                Location = new Point(278, 282),
                Cursor = Cursors.Hand
            };
            btnStop.FlatAppearance.BorderSize = 0;

            btnPlay.Click += (s, e) =>
            {
                if (!isReading && !isPaused)
                {
                    isReading = true;
                    isPaused = false;
                    btnPlay.Text = "⏸️ Pause";
                    reader.StartReading(contentText, selectedLang);
                }
                else if (!isPaused)
                {
                    isPaused = true;
                    btnPlay.Text = "▶️ Lanjutkan";
                    reader.Pause();
                }
                else
                {
                    isPaused = false;
                    btnPlay.Text = "⏸️ Pause";
                    reader.Resume();
                }
            };

            btnStop.Click += (s, e) =>
            {
                isReading = false;
                isPaused = false;
                btnPlay.Text = "▶️ Putar Suara";
                lblProgress.Text = "Dihentikan";
                reader.Stop();
            };

            reader.OnProgressUpdate += (idx, total, textP) =>
            {
                if (dlg.IsDisposed || !dlg.IsHandleCreated) return;
                dlg.BeginInvoke(new Action(() =>
                {
                    lblProgress.Text = $"Membaca paragraf {idx} dari {total}...";
                    txtPreview.Text = textP;
                }));
            };

            reader.OnStateChange += (playing, paused) =>
            {
                if (dlg.IsDisposed || !dlg.IsHandleCreated) return;
                dlg.BeginInvoke(new Action(() =>
                {
                    isReading = playing || paused;
                    isPaused = paused;
                    btnPlay.Text = paused ? "▶️ Lanjutkan" : (playing ? "⏸️ Pause" : "▶️ Putar Suara");
                    if (!playing && !paused && lblProgress.Text != "Dihentikan")
                        lblProgress.Text = "Selesai membaca seluruh dokumen.";
                }));
            };

            dlg.FormClosing += (s, e) => reader.Dispose();
            root.Controls.Add(btnPlay);
            root.Controls.Add(btnStop);
            dlg.ShowDialog(owner);
        }
    }
}
