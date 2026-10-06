using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Guarda dos tokens de design. Um <c>{StaticResource Chave}</c> com a chave errada
    /// COMPILA sem reclamar: o erro só aparece como XamlParseException quando a tela abre —
    /// e, se estiver dentro de um ControlTemplate, só quando aquele controle é criado.
    /// <para>
    /// Este ensaio lê o XAML como texto e cobra que toda chave referenciada exista. Não
    /// precisa de STA, de janela nem de Application: é análise de fonte, e pega inclusive as
    /// referências dentro de template que o carregamento do dicionário não resolveria.
    /// </para>
    /// </summary>
    public class ThemeResourcesTests
    {
        private static readonly Regex Referencia =
            new(@"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_.]*)\s*\}", RegexOptions.Compiled);

        private static readonly Regex Definicao =
            new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

        /// <summary>
        /// Chaves que o próprio WPF define. Não estão em nenhum XAML nosso e não são erro.
        /// Manter vazio enquanto der: cada entrada aqui é uma chave que o ensaio deixa de
        /// conferir.
        /// </summary>
        private static readonly HashSet<string> DoFramework = new(StringComparer.Ordinal);

        private static string RaizDoProjeto()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "AIBWindows")))
                dir = dir.Parent;

            dir.Should().NotBeNull("o ensaio precisa achar a raiz do repositório a partir do bin");
            return dir!.FullName;
        }

        private static IReadOnlyList<string> ArquivosXaml()
        {
            string raiz = Path.Combine(RaizDoProjeto(), "AIBWindows");
            return Directory.GetFiles(raiz, "*.xaml", SearchOption.AllDirectories)
                            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                            .OrderBy(p => p, StringComparer.Ordinal)
                            .ToList();
        }

        private static HashSet<string> ChavesDefinidas()
        {
            var chaves = new HashSet<string>(StringComparer.Ordinal);
            foreach (string arquivo in ArquivosXaml())
                foreach (Match m in Definicao.Matches(File.ReadAllText(arquivo)))
                    chaves.Add(m.Groups[1].Value);
            return chaves;
        }

        [Fact]
        public void TodoStaticResourceReferenciadoExiste()
        {
            var definidas = ChavesDefinidas();
            var orfas = new List<string>();

            foreach (string arquivo in ArquivosXaml())
            {
                string texto = File.ReadAllText(arquivo);
                foreach (Match m in Referencia.Matches(texto))
                {
                    string chave = m.Groups[1].Value;
                    if (definidas.Contains(chave) || DoFramework.Contains(chave)) continue;
                    orfas.Add($"{Path.GetFileName(arquivo)} -> {chave}");
                }
            }

            orfas.Should().BeEmpty(
                "StaticResource com chave inexistente só estoura quando a tela abre");
        }

        [Fact]
        public void TokensTrazemAsChavesQueAsSpecsExigem()
        {
            // Amostra do §1 das duas specs. Não é a lista inteira — é o conjunto que, se
            // sumir, deixa a tela sem a identidade que o contrato chama de obrigatória.
            var obrigatorias = new[]
            {
                "GlassBrush",          // O1/O2: vidro sólido, nunca blur
                "NeonBrush",           // moldura de 4 paradas
                "PrimaryBrush",        // bolha do usuário, Salvar, switch ligado
                "InputFrameBrush",     // moldura do input, 3 paradas
                "WindowShadow",        // sem ela a janela não flutua
                "CardShadow",
                "RadiusBubble",        // r18 UNIFORME
                "RadiusWindowOuter",
                "RadiusWindowInner",
                "TextStrongBrush", "TextBodyBrush", "TextSecondaryBrush", "TextMutedBrush",
                "AccentLilacBrush", "SuccessBrush", "DangerBrush", "WarnBrush", "MagentaBrush",
                "MonoFontFamily",
                "ChipMinHeight",       // O9: chip pronto e em execução na mesma altura
                "IconTrackMaxWidth",   // 5 × 22 + 4 × 8
                "ControlColumnWidth"   // O6: coluna de 210px
            };

            // Cores num arquivo por tema, o resto no Tokens.xaml: toda chave obrigatória tem de
            // existir em CADA tema (somado ao Tokens.xaml).
            string temas = Path.Combine(RaizDoProjeto(), "AIBWindows", "Themes");
            string formas = File.ReadAllText(Path.Combine(temas, "Tokens.xaml"));

            foreach (string arquivo in Directory.GetFiles(temas, "Cores.*.xaml"))
            {
                string tokens = formas + File.ReadAllText(arquivo);
                foreach (string chave in obrigatorias)
                    tokens.Should().Contain($"x:Key=\"{chave}\"", $"a spec exige o token {chave} ({Path.GetFileName(arquivo)})");
            }
        }

        [Fact]
        public void MolduraNeonSegueAOrdemEOsOffsetsDaSpec()
        {
            // §1 `neon`: roxo 0.00, azul 0.35, magenta 0.65, laranja 1.00. A ordem é
            // obrigatória (chat O2 / configurações O3) e já foi escrita errada antes, com
            // 0.33/0.66, na SettingsWindow.
            // A moldura neon é a marca: igual nos dois temas.
            foreach (string arquivo in Directory.GetFiles(
                         Path.Combine(RaizDoProjeto(), "AIBWindows", "Themes"), "Cores.*.xaml"))
            {
                string tokens = File.ReadAllText(arquivo);

                int inicio = tokens.IndexOf("x:Key=\"NeonBrush\"", StringComparison.Ordinal);
                inicio.Should().BeGreaterThan(-1);
                string bloco = tokens[inicio..tokens.IndexOf("</LinearGradientBrush>", inicio, StringComparison.Ordinal)];

                bloco.Should().Contain("#9B51E0").And.Contain("Offset=\"0.00\"");
                bloco.Should().Contain("#3182CE").And.Contain("Offset=\"0.35\"");
                bloco.Should().Contain("#D53F8C").And.Contain("Offset=\"0.65\"");
                bloco.Should().Contain("#E28743").And.Contain("Offset=\"1.00\"");
            }
        }
    }
}
