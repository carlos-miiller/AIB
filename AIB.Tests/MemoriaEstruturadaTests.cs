using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using AIB.Services.Memory;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O formato por seções de capítulo e ato: Estado e Combinado por código, Objetivo e Aprendido
    /// pelo modelo com conferência, e o ato por fusão. O cenário imita a conversa real das
    /// assinaturas, onde o agente apagou a pasta de trabalho e o resumo antigo inventou a causa.
    /// </summary>
    public class MemoriaEstruturadaTests
    {
        private const string Pasta = @"C:\Users\x\TEMP\emails fisio";

        private static int _id;

        private static ChatMessage[] Chamada(string ferramenta, object args, string resultado)
        {
            string id = "c" + Interlocked.Increment(ref _id);
            return new ChatMessage[]
            {
                new AssistantChatMessage(new[] { ChatToolCall.CreateFunctionToolCall(id, ferramenta, BinaryData.FromString(JsonSerializer.Serialize(args))) }),
                ChatMessage.CreateToolMessage(id, resultado)
            };
        }

        private static Turn Turno(int indice, string fala, params ChatMessage[][] chamadas)
        {
            var mensagens = new List<ChatMessage> { ChatMessage.CreateUserMessage(fala) };
            foreach (var c in chamadas) mensagens.AddRange(c);
            mensagens.Add(ChatMessage.CreateAssistantMessage("ok"));
            return new Turn(indice, mensagens);
        }

        private static string Ok(string caminho) => $"SUCESSO: Arquivo salvo corretamente em '{caminho}'.";

        /// <summary>Capítulo 1: lê o que o usuário tinha, cria assinaturas à mão e o script.</summary>
        private static IReadOnlyList<Turn> Capitulo1() => new[]
        {
            Turno(0, $"dê uma olhada nos arquivos em \"{Pasta}\"",
                Chamada("read", new { path = Pasta }, $"'{Pasta}' é uma PASTA, com 0 subpasta(s) e 3 arquivo(s):"),
                Chamada("read", new { path = Pasta + @"\templateassinatura.html" }, "     1\t<div>")),
            Turno(1, "altere o horário para Sábados: 12:00 ~ 14:00",
                Chamada("write", new { path = Pasta + @"\thais_araujo.html", content = "<div>" }, Ok(Pasta + @"\thais_araujo.html"))),
            Turno(2, "crie um script para gerar as assinaturas",
                Chamada("write", new { path = Pasta + @"\gerar-assinaturas.ps1", content = "# script" }, Ok(Pasta + @"\gerar-assinaturas.ps1")),
                Chamada("shell", new { command = $"powershell -File \"{Pasta}\\gerar-assinaturas.ps1\"" }, "Criado: Thais.html"),
                Chamada("shell", new { command = $"dir \"{Pasta}\"" }, "Thais.html  4260"))
        };

        /// <summary>Capítulo 2: apaga a pasta inteira, o script falha, recria o template.</summary>
        private static IReadOnlyList<Turn> Capitulo2() => new[]
        {
            Turno(3, "o script quebra os acentos, corrija",
                Chamada("shell", new { command = $"Remove-Item \"{Pasta}\" -Recurse -Force" }, "Limpeza concluída."),
                Chamada("shell", new { command = $"powershell -File \"{Pasta}\\gerar-assinaturas.ps1\"" },
                    $"ERRO (código de saída 1): O argumento '{Pasta}\\gerar-assinaturas.ps1' para o parâmetro -File não existe."),
                Chamada("write", new { path = Pasta + @"\templateassinatura.html", content = "<div>novo</div>" }, Ok(Pasta + @"\templateassinatura.html")))
        };

        // ─────────────────────────────────────────────────────────────────────
        // Estado
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Estado_DobraPorArquivo_ELeiturasNaoViramItem()
        {
            var estado = EstadoDoTrecho.Montar(Capitulo1());
            string texto = estado.Render("do capítulo 1");

            texto.Should().Contain($"(pasta {Pasta})");
            texto.Should().Contain("templateassinatura.html: já existia");
            texto.Should().Contain("thais_araujo.html: criado pelo agente");
            texto.Should().Contain("gerar-assinaturas.ps1: criado pelo agente");
            texto.Should().Contain("efeitos em disco não rastreados", "o script não diz o que gravou");
            texto.Should().NotContain("`dir", "listagem é consulta, não estado");
            texto.Should().Contain("Consultados:");
        }

        [Fact]
        public void Estado_ApagarAPasta_MarcaOQueEstavaDentro_EAFalhaVemComOErro()
        {
            var estado = EstadoDoTrecho.Montar(Capitulo1().Concat(Capitulo2()).ToList());
            string texto = estado.Render("do ato 1");

            texto.Should().Contain("a própria pasta: APAGADO pelo agente");
            texto.Should().Contain("thais_araujo.html: apagado junto com a pasta");
            texto.Should().Contain("templateassinatura.html: recriado pelo agente",
                "é o fato que o resumo antigo trocou por \"caracteres especiais no caminho\"");
            texto.Should().Contain("última: FALHOU — O argumento");
            texto.Should().NotContain("Remove-Item", "comando de apagar não sai pronto para repetir");
        }

        [Fact]
        public void Fundir_PropagaAApagaoDoCapituloSeguinteParaOAnterior()
        {
            // Cada capítulo só conhece os próprios arquivos. É a fusão, no ato, que descobre que a
            // pasta apagada no capítulo 2 levou o que o capítulo 1 criou.
            var fundido = EstadoDoTrecho.Fundir(new[] { EstadoDoTrecho.Montar(Capitulo1()), EstadoDoTrecho.Montar(Capitulo2()) });

            fundido.Itens.Single(i => i.Alvo.EndsWith("thais_araujo.html")).Situacao.Should().Be("apagado junto com a pasta");
            fundido.Itens.Single(i => i.Alvo.EndsWith("gerar-assinaturas.ps1")).Situacao.Should().Be("apagado junto com a pasta");
        }

        [Fact]
        public void ApagarSemAlvoLiteral_NaoAfirmaOQueFoiApagado()
        {
            var estado = EstadoDoTrecho.Montar(new[]
            {
                Turno(0, "limpe", Chamada("shell", new { command = $"Remove-Item \"{Pasta}\\*.html\"" }, "ok"))
            });

            estado.Itens.Should().ContainSingle().Which.Alvo.Should().Be("(comando que apaga arquivos)");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Combinado
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Combinado_CopiaOsPedidosComValor_ENaoOsSemValor()
        {
            var falas = Combinados.Extrair(new[]
            {
                Turno(0, "leia os arquivos"),
                Turno(1, "altere o horário para Sábados: 12:00 ~ 14:00"),
                Turno(2, "continue")
            });

            falas.Should().ContainSingle().Which.Texto.Should().Be("altere o horário para Sábados: 12:00 ~ 14:00");
            Combinados.Render(falas).Should().Contain("(turno 2) \"altere o horário para Sábados: 12:00 ~ 14:00\"");
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que o modelo escreve
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void LicaoQueCitaValorSemOrigem_EhDescartada()
        {
            // O resumo antigo explicou a falha com um fato que não estava no trecho. A conferência
            // não entende o sentido, mas pega o valor inventado.
            const string fonte = "RESULTADO (FALHOU, shell): O argumento 'C:\\x\\gerar.ps1' para o parâmetro -File não existe.";
            var (objetivo, aprendido, _) = Compactor.LerSecoes(
                "**OBJETIVO:** gerar as assinaturas\nAPRENDIDO:\n- gerar.ps1 não existia porque a pasta foi apagada\n- o caminho C:\\y\\outro.ps1 tinha acentos\nPENDENTE: nenhuma",
                fonte);

            objetivo.Should().Be("gerar as assinaturas");
            aprendido.Should().ContainSingle().Which.Should().StartWith("gerar.ps1 não existia");
        }

        [Fact]
        public async Task SoCodigo_NaoChamaOModelo_EOObjetivoEhAFalaLiteral()
        {
            var provider = new ProviderQueConta();
            var capitulo = await new Compactor(provider, comModelo: false).SummarizeAsync(0, Capitulo1(), default);

            provider.Chamadas.Should().Be(0, "o modo só código existe para o modelo local lento");
            capitulo.Objetivo.Should().Be($"pedido do turno 1: \"dê uma olhada nos arquivos em \"{Pasta}\"\"");
            capitulo.Render().Should().Contain("Estado ao fim do capítulo 1").And.Contain("Sábados: 12:00 ~ 14:00");
        }

        [Fact]
        public async Task Ato_EhFusao_EOsLiteraisSobemIntactos()
        {
            var compactor = new Compactor(new ProviderQueConta(), comModelo: false);
            var c1 = await compactor.SummarizeAsync(0, Capitulo1(), default);
            var c2 = await compactor.SummarizeAsync(1, Capitulo2(), default) with { Index = 1 };

            var ato = await compactor.PromoteAsync(0, new[] { c1, c2 }, default);
            string texto = ato.Render();

            ato.Versao.Should().Be(BlocoEstruturado.Versao);
            texto.Should().Contain("Sábados: 12:00 ~ 14:00", "no ato antigo o horário pedido tinha sumido");
            texto.Should().Contain("thais_araujo.html: apagado junto com a pasta");
            texto.Should().NotContain("### Capítulo");
        }

        [Fact]
        public void BlocoMaiorQueACota_EncolheMasNuncaSome()
        {
            var falas = Enumerable.Range(0, 16).Select(i => new FalaCombinada(i, $"pedido {i} com valor {i * 100} " + new string('x', 200))).ToList();
            var contador = new TokenCounter();

            string bloco = BlocoEstruturado.Fit(120, contador, "### Ato 1", "objetivo curto", falas,
                EstadoDoTrecho.Montar(Capitulo1()), "do ato 1", new[] { "lição" });

            bloco.Should().StartWith("### Ato 1");
            contador.CountText(bloco).Should().BeLessThanOrEqualTo(120);
        }

        private static EstadoDoTrecho EstadoCom(int itens) => new(
            Enumerable.Range(0, itens)
                .Select(i => new ItemDeEstado($@"{Pasta}\arquivo{i}.html", ItemDeEstado.TipoArquivo, "criado pelo agente", null, i))
                .ToList(),
            Array.Empty<string>(), 0);

        [Fact]
        public void Ato_UsaOTetoDeItensDoAto_NoRenderENoFit()
        {
            // O teto do ato existia, mas o Render do estado cortava sempre em vinte: o ato de
            // vinte e cinco itens saía com "e mais 5" como se fosse capítulo.
            var ato = new Act(0, "2026-09-18T00:00:00Z", 0, 3, 0, 9, "", Array.Empty<Artifact>(),
                Versao: BlocoEstruturado.Versao, Objetivo: "objetivo", Estado: EstadoCom(25));

            ato.Render().Should().NotContain("e mais").And.Contain("arquivo0.html");
            ato.Render(100_000, new TokenCounter()).Should().NotContain("e mais").And.NotContain("omitido");

            EstadoCom(25).Render("do capítulo 1").Should().Contain("e mais 5", "o capítulo continua no teto de vinte");
        }

        [Fact]
        public void LerSecoes_GuardaAsLinhasQueOAtoPediu()
        {
            // O ato na nuvem pede dez lições; com o corte fixo em cinco, metade sumia.
            string texto = "OBJETIVO: x\nAPRENDIDO:\n" + string.Join("\n", Enumerable.Range(1, 8).Select(i => $"- lição simples número um{new string('a', i)}"));

            Compactor.LerSecoes(texto, "fonte", 0, maxLicoes: 10).Aprendido.Should().HaveCount(8);
            Compactor.LerSecoes(texto, "fonte").Aprendido.Should().HaveCount(Compactor.LicoesDoCapitulo);
        }

        [Fact]
        public void Estado_ComandoQueFalhaEDepoisDaCerto_EhUmItemSo_PelaAssinatura()
        {
            // O mesmo casamento do Pendente: com o texto cru como chave, a versão com pipe que
            // falhou ficava no Estado como FALHOU depois de a versão sem pipe dar certo.
            var estado = EstadoDoTrecho.Montar(new[]
            {
                Turno(0, "rode o teste",
                    Chamada("shell", new { command = "php teste.php | Select-Object -Last 20" }, "ERRO (código de saída 1): falhou"),
                    Chamada("shell", new { command = "php teste.php" }, "tudo certo"))
            });

            var item = estado.Itens.Should().ContainSingle().Which;
            item.Situacao.Should().Be("ok");
            item.Alvo.Should().Be("php teste.php", "fica o texto da última execução");
            item.Vezes.Should().Be(2);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Esconder resultados antigos
        // ─────────────────────────────────────────────────────────────────────

        private static List<ChatMessage> Historico(int turnos)
        {
            var h = new List<ChatMessage> { ChatMessage.CreateSystemMessage("alma") };
            for (int i = 0; i < turnos; i++)
            {
                h.Add(ChatMessage.CreateUserMessage($"pergunta {i}"));
                h.AddRange(Chamada("read", new { path = $@"C:\a{i}.txt" }, new string('x', 1000)));
                h.Add(ChatMessage.CreateAssistantMessage("li"));
            }
            return h;
        }

        [Fact]
        public void Esconder_TrocaSoOsResultadosAntigos_EmDegraus()
        {
            var h = Historico(10);

            var copia = ResultadosAntigos.Esconder(h, manterTurnos: 4);

            // 10 turnos, 4 ficam: 6 podem sair, mas a fronteira anda de 4 em 4 — saem 4.
            var ferramentas = copia.OfType<ToolChatMessage>().Select(m => m.Content[0].Text).ToList();
            ferramentas.Take(4).Should().OnlyContain(t => t.StartsWith("[resultado antigo de read omitido"));
            ferramentas.Skip(4).Should().OnlyContain(t => t.Length == 1000);
            copia.Count.Should().Be(h.Count, "pedido, chamada e fala ficam");
            h.OfType<ToolChatMessage>().Select(m => m.Content[0].Text).Should().OnlyContain(t => t.Length == 1000, "o histórico não muda");
        }

        [Fact]
        public void Esconder_DesligadoOuPoucosTurnos_DevolveOMesmoHistorico()
        {
            var h = Historico(5);
            ResultadosAntigos.Esconder(h, 0).Should().BeSameAs(h);
            ResultadosAntigos.Esconder(h, 4).Should().BeSameAs(h, "sobra 1 turno, menos que um degrau");
        }

        [Fact]
        public void ConfiguracoesDeMemoria_SaoSaneadas()
        {
            var s = new UserAppSettings { TurnosPorCapitulo = 99, CapitulosPorAto = 1, EsconderResultadosDepoisDe = -3 }.Sanear();

            s.TurnosPorCapitulo.Should().Be(20);
            s.CapitulosPorAto.Should().Be(2);
            s.EsconderResultadosDepoisDe.Should().Be(0);
            new UserAppSettings().MemoriaComModelo.Should().BeTrue();
        }
        private sealed class ProviderQueConta : IChatProvider
        {
            public int Chamadas { get; private set; }
            public string Name => "Fake";
            public string Model => "fake";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t, ChatRequestOptions o,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                yield break;
            }

            public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t, ChatRequestOptions o, CancellationToken ct)
            {
                Chamadas++;
                return Task.FromResult(new ChatCompletionResult("OBJETIVO: x", null, null));
            }

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
