using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Constrói cada janela de verdade e a força a medir, arranjar e desenhar.
    /// <para>
    /// O <see cref="ThemeResourcesTests"/> confere as chaves como texto, mas não pega tudo:
    /// um <c>{StaticResource}</c> dentro de um ControlTemplate só é resolvido quando aquele
    /// controle é criado, e um Setter com valor de tipo errado só estoura no parse do BAML.
    /// Renderizar a janela exercita os dois caminhos.
    /// </para>
    /// <para>
    /// Definindo <c>AIB_UI_PNG=1</c>, cada janela também é salva como PNG na pasta indicada
    /// por <c>AIB_UI_PNG_DIR</c> (ou no temporário). Serve para conferir o desenho contra as
    /// specs sem precisar abrir o app.
    /// </para>
    /// </summary>
    public class WindowSmokeTests
    {
        /// <summary>
        /// WPF exige STA. O xUnit roda em MTA, então cada ensaio abre a sua própria thread —
        /// é mais simples que trazer um pacote só para isso, e deixa a falha visível como
        /// exceção normal.
        /// </summary>
        private static void EmSta(Action acao)
        {
            Exception? falha = null;

            var t = new Thread(() =>
            {
                try { acao(); }
                catch (Exception ex) { falha = ex; }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("a janela não pode travar ao montar");

            if (falha != null) throw new Xunit.Sdk.XunitException(
                $"a janela não montou: {falha.GetType().Name}: {falha.Message}");
        }

        /// <summary>
        /// Aplica os dicionários do App.xaml. Sem isto não existe Application.Current e todo
        /// StaticResource de Themes/ some.
        /// </summary>
        private static void GarantirRecursos()
        {
            var app = Application.Current ?? new Application();

            if (app.Resources.MergedDictionaries.Count == 0)
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/AIB;component/Themes/Controls.xaml")
                });
            }
        }

        private static void Desenhar(Window janela, string nome, double largura = 0, double altura = 0)
        {
            double w = largura > 0 ? largura : janela.Width;
            double h = altura > 0 ? altura : janela.Height;

            // O conteúdo, e não a Window. RenderTargetBitmap sobre uma Window que nunca foi
            // exibida desenha uma imagem em branco: a árvore visual da janela só existe depois
            // do Show(). O conteúdo medido e arranjado à mão, não.
            if (janela.Content is not FrameworkElement raiz) return;

            raiz.Measure(new Size(w, h));
            raiz.Arrange(new Rect(0, 0, w, h));
            raiz.UpdateLayout();

            if (Environment.GetEnvironmentVariable("AIB_UI_PNG") != "1") return;

            string dir = Environment.GetEnvironmentVariable("AIB_UI_PNG_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Fundo escuro no lugar do desktop, como o mock faz: sem ele a janela de vidro
                // e a sombra ficam invisíveis sobre o alfa zerado do PNG.
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x1E)),
                                 null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(raiz), null, new Rect(0, 0, w, h));
            }

            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));

            using var fs = File.Create(Path.Combine(dir, nome + ".png"));
            png.Save(fs);
        }

        /// <summary>
        /// SettingsService apontado para um arquivo descartável. O ensaio NUNCA pode escrever
        /// nas configurações reais do usuário.
        /// </summary>
        private static SettingsService ServicoDescartavel()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "aib-ensaio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            return new SettingsService(Path.Combine(tmp, "settings.json"));
        }

        [Fact]
        public void TelaDeConfiguracoes_MontaEDesenha()
        {
            EmSta(() =>
            {
                GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                Desenhar(janela, "configuracoes");

                // Segunda passada bem mais alta. A área de seções é a única linha `*`, então
                // ela cresce e mostra os nove campos de uma vez — inclusive os dois switches,
                // que na altura de desenho ficam abaixo da dobra.
                Desenhar(janela, "configuracoes-inteira", 700, 1080);

                janela.Close();
            });
        }

        [Fact]
        public void TelaDeConfiguracoes_SalvarNasceDesabilitado()
        {
            // O7 / A8: sem isso o usuário não sabe se mexeu em algo.
            EmSta(() =>
            {
                GarantirRecursos();
                var janela = new SettingsWindow(ServicoDescartavel());
                janela.Measure(new Size(janela.Width, janela.Height));

                var salvar = (System.Windows.Controls.Button)janela.FindName("SaveButton");
                salvar.Should().NotBeNull();
                salvar.IsEnabled.Should().BeFalse("Salvar só habilita quando algo muda");

                janela.Close();
            });
        }

        [Fact]
        public void ConfirmacaoDestrutiva_Monta()
        {
            EmSta(() =>
            {
                GarantirRecursos();
                var janela = new ConfirmDialog();
                janela.Measure(new Size(520, 400));
                janela.Close();
            });
        }
    }
}
