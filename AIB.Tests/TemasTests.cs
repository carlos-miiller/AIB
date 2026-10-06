using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using AIB.Ui;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Tema claro e escuro: um dicionário de cores por tema, com as MESMAS chaves.
    /// <para>
    /// Uma chave que existe só no escuro compila e passa em todo teste — a suíte monta no
    /// escuro — e só explode quando alguém escolhe o claro e abre a tela que a usa.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class TemasTests
    {
        private static string PastaDosTemas()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "AIBWindows")))
                dir = dir.Parent;
            dir.Should().NotBeNull("a saída do build precisa ficar dentro do repositório");
            return Path.Combine(dir!.FullName, "AIBWindows", "Themes");
        }

        private static string[] Chaves(string arquivo) =>
            Regex.Matches(File.ReadAllText(arquivo), "x:Key=\"([^\"]+)\"")
                 .Select(m => m.Groups[1].Value).OrderBy(k => k).ToArray();

        [Fact]
        public void OsDoisTemas_TemAsMesmasChaves()
        {
            string pasta = PastaDosTemas();
            var escuro = Chaves(Path.Combine(pasta, "Cores.Escuro.xaml"));
            var claro = Chaves(Path.Combine(pasta, "Cores.Claro.xaml"));

            escuro.Should().NotBeEmpty();
            claro.Should().Equal(escuro, "chave que falta num tema só explode na tela que a usa");
        }

        [Fact]
        public void CorNaoMoraNoTokens()
        {
            // O Tokens.xaml vale para os dois temas: uma cor ali seria igual nos dois.
            File.ReadAllText(Path.Combine(PastaDosTemas(), "Tokens.xaml"))
                .Should().NotContainAny("SolidColorBrush", "GradientBrush", "DropShadowEffect");
        }

        [Theory]
        [InlineData("Escuro", null, "Escuro")]
        [InlineData("Claro", null, "Claro")]
        [InlineData("claro", false, "Claro")]
        [InlineData("Sistema", true, "Claro")]
        [InlineData("Sistema", false, "Escuro")]
        [InlineData("Sistema", null, "Escuro")]
        [InlineData(null, true, "Escuro")]
        [InlineData("roxo", true, "Escuro")]
        public void OTemaEfetivo(string? escolha, bool? windowsClaro, string esperado)
        {
            // Valor desconhecido (arquivo antigo, corrompido) cai no escuro, o original.
            Tema.Efetivo(escolha, windowsClaro).Should().Be(esperado);
        }

        [Theory]
        [InlineData("Claro", "Claro")]
        [InlineData("Sistema", "Sistema")]
        [InlineData("roxo", "Escuro")]
        public void OSeletor_MostraOTemaGravado(string gravado, string mostrado)
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                string pasta = Path.Combine(Path.GetTempPath(), "aib-tema-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(pasta);
                var servico = new AIB.Services.SettingsService(Path.Combine(pasta, "s.json"));
                var s = servico.LoadSettings();
                s.Tema = gravado;
                servico.SaveSettings(s);

                var janela = new SettingsWindow(servico);
                ((System.Windows.Controls.ComboBox)janela.FindName("TemaComboBox")).SelectedValue
                    .Should().Be(mostrado);
                janela.Close();
            });
        }

        [Fact]
        public void NoClaro_AsJanelasMontam()
        {
            // Monta as telas com o claro carregado e devolve o escuro no fim: os recursos do
            // Application são um só para a suíte.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var recursos = Application.Current.Resources;
                var antes = recursos.MergedDictionaries.ToList();

                try
                {
                    Tema.Carregar(recursos, Tema.Claro);

                    ((System.Windows.Media.SolidColorBrush)recursos["GlassBrush"]).Color.R
                        .Should().BeGreaterThan(0xE0, "o vidro claro é claro");

                    var chat = JanelaDeEnsaio.Nova();
                    chat.Measure(new Size(820, 605));
                    chat.Close();

                    new SidePanelWindow().Close();
                    string pasta = Path.Combine(Path.GetTempPath(), "aib-tema-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(pasta);
                    new SettingsWindow(new AIB.Services.SettingsService(Path.Combine(pasta, "s.json"))).Close();
                }
                finally
                {
                    recursos.MergedDictionaries.Clear();
                    foreach (var d in antes) recursos.MergedDictionaries.Add(d);
                }
            });
        }
    }
}
