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

        /// <summary>
        /// Caso real (shadow, capítulo 105): vinte chamadas "skill bitrix -Acao adicionar -Id 421099
        /// -Texto …" viravam vinte linhas do Estado, 60% do capítulo, todas com "efeitos em disco
        /// não rastreados", que para uma API é falso. A mesma skill, ação e alvo viram uma linha,
        /// com a contagem, as recusas e a última chamada.
        /// </summary>
        [Fact]
        public void Estado_ChamadasDaMesmaSkill_ViramUmaLinha()
        {
            object Add(string texto) => new { skill_name = "bitrix", arguments = $"-Acao adicionar -Id 421099 -Texto \"{texto}\" -Grupo 495217" };
            var estado = EstadoDoTrecho.Montar(new[]
            {
                Turno(0, "cadastra",
                    Chamada("skill", Add("SP - Campinas - Celular Geral"), "Item adicionado."),
                    Chamada("skill", Add("SP - Itaim Bibi - Celular Geral"), ToolRegistry.RecusaDoUsuario),
                    Chamada("skill", Add("SP - Ribeirão Preto - Celular Geral"), "Item adicionado."),
                    Chamada("skill", new { skill_name = "bitrix", arguments = "-Acao checklist -Id 421099" }, "1. SP - Campinas"))
            });
            string texto = estado.Render("do capítulo 1");

            texto.Should().Contain("skill bitrix -Acao adicionar -Id 421099 (3×): última: ok — 1 recusada pelo usuário; última: -Texto \"SP - Ribeirão Preto - Celular Geral\" -Grupo 495217");
            texto.Should().Contain("skill bitrix -Acao checklist -Id 421099: última: ok");
            texto.Should().NotContain("efeitos em disco");
            estado.Itens.Should().HaveCount(2);
        }

        /// <summary>
        /// Caso real (shadow, turnos 512 e 549): o agente criou duas planilhas pela ferramenta doc
        /// (um create e três edits cada) e nenhuma aparecia no Estado do capítulo nem do ato. O
        /// Montar não tinha caso para "doc", e a memória não sabia que os arquivos existiam.
        /// </summary>
        [Fact]
        public void Estado_PlanilhaCriadaEEditadaPeloDoc_EntraComoArquivoDoAgente()
        {
            string xlsx = Pasta + @"\dashboard-tarefas-ti-mock.xlsx";
            string docx = Pasta + @"\ata.docx";
            var estado = EstadoDoTrecho.Montar(new[]
            {
                Turno(0, "monte uma planilha de tarefas",
                    Chamada("doc", new { action = "create", path = xlsx, content = new { sheets = Array.Empty<object>() } },
                        $"SUCESSO: Planilha criada em '{xlsx}'."),
                    Chamada("doc", new { action = "edit", path = xlsx, ops = new object[] { new { cell = "A1", value = "x" }, new { cell = "B1", value = "y" } } },
                        $"SUCESSO: 2 alterações em '{xlsx}'."),
                    Chamada("doc", new { action = "read", path = docx }, "¶1 Ata da reunião"),
                    Chamada("doc", new { action = "help", topic = "criar" }, "Ajuda"))
            });
            string texto = estado.Render("do capítulo 1");

            texto.Should().Contain("dashboard-tarefas-ti-mock.xlsx: criado pelo agente (2 alterações)",
                "editar o que o agente criou mantém a origem");
            texto.Should().Contain("ata.docx: já existia", "ler pelo doc prova que o arquivo existia, como o read");
            estado.Consultas.Should().Be(1, "só a leitura conta; help não tem arquivo");
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

        /// <summary>
        /// Caso real (shadow, turno 558): a fala longa virou duas linhas no Combinado, uma só com
        /// "[terça-feira - 27/10/2026 | 11:42]". O carimbo do orbe sempre tem número, então passava
        /// no filtro de valor, tomava uma das três vagas da fala longa e fazia "continue" carimbado
        /// entrar no Combinado.
        /// </summary>
        [Fact]
        public void Combinado_IgnoraOCarimboDoOrbe()
        {
            string longa = "Ellen modifica essa tarefa que criei 421099\n"
                         + new string('x', 300) + "\n"
                         + "prazo 30/10\n"
                         + "responsável: 2 pessoas";
            var falas = Combinados.Extrair(new[]
            {
                Turno(0, ConversaDoOrbe.Carimbar("continue", new DateTime(2026, 10, 27, 11, 40, 0))),
                Turno(1, ConversaDoOrbe.Carimbar(longa, new DateTime(2026, 10, 27, 11, 42, 0)))
            });

            falas.Select(f => f.Texto).Should().Equal(
                "Ellen modifica essa tarefa que criei 421099", "prazo 30/10", "responsável: 2 pessoas");
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

        // ─────────────────────────────────────────────────────────────────────
        // Resumo e frases memoráveis
        // ─────────────────────────────────────────────────────────────────────

        private static Turn TurnoCarimbado(int indice, string fala, string resposta) => new(indice, new ChatMessage[]
        {
            ChatMessage.CreateUserMessage(ConversaDoOrbe.Carimbar(fala, new DateTime(2026, 10, 27, 12, 8, 0))),
            ChatMessage.CreateAssistantMessage(resposta)
        });

        /// <summary>
        /// Caso real (shadow, capítulo 105): a memória perdia a conversa — a pergunta do usuário
        /// sobre a ordem da lista e a resposta da agente não ficavam em lugar nenhum. O resumidor
        /// passou a escrever RESUMO e FRASES. No teste com o Gemini, ele pôs cada valor na linha de
        /// baixo do rótulo, e o leitor antigo descartava o objetivo; e frase só fica se estiver no
        /// trecho, palavra por palavra.
        /// </summary>
        [Fact]
        public async Task ResumoEFrases_EntramNoCapitulo_EFraseInventadaSai()
        {
            const string resposta = "RESUMO:\nCarlos se desculpou por negar uma ação e Ellen levou na brincadeira.\n\n"
                                  + "FRASES:\n- Carlos: \"acho que neguei essa sua ultima ação sem querer\"\n"
                                  + "- Ellen: \"uma frase que ninguém disse\"\n\n"
                                  + "OBJETIVO:\nReorganizar a lista de lojas.\n\nAPRENDIDO: nenhum\n\nPENDENTE:\ncriar as demais lojas";
            var turnos = new[]
            {
                TurnoCarimbado(0, "ellen desculpa, acho que neguei essa sua ultima ação sem querer",
                    "Não esquenta, Carlos! O botão de negar fica bem ali no clique rápido.")
            };

            var capitulo = await new Compactor(new ProviderQueConta(resposta)).SummarizeAsync(0, turnos, default);

            capitulo.Resumo.Should().Be("Carlos se desculpou por negar uma ação e Ellen levou na brincadeira.");
            capitulo.Frases.Should().Equal("Carlos: \"acho que neguei essa sua ultima ação sem querer\"");
            capitulo.Objetivo.Should().BeNull("com resumo, a primeira frase dele situa o trecho");
            capitulo.Pendencias!.Should().ContainSingle(p => p.Texto == "criar as demais lojas",
                "o valor na linha de baixo do rótulo vale");

            string texto = capitulo.Render();
            texto.IndexOf("Resumo:").Should().BeLessThan(texto.IndexOf("Frases memoráveis:"));
            texto.Should().NotContain("Objetivo:");
        }

        /// <summary>
        /// Pedido do usuário: o Combinado guarda só fala que pede ou decide. No capítulo 105 do
        /// shadow, o filtro por valor deixava "foi mal, ellen. te neguei de novo sem querer" e
        /// perdia "pode fazer dois grupos para eu ver como ficaria?". O código numera todas as falas,
        /// o modelo escolhe por número e o texto copiado é o do usuário. Sem a linha COMBINADO, fica
        /// o Combinado de código: falha do modelo não apaga pedido.
        /// </summary>
        [Fact]
        public async Task Combinado_EscolhidoPorNumero_CopiaAFalaDoUsuario()
        {
            var turnos = new[]
            {
                TurnoCarimbado(0, "pode fazer dois grupos para eu ver como ficaria?", "Feito."),
                TurnoCarimbado(1, "foi mal, ellen. te neguei de novo sem querer em 2 itens", "Sem problema.")
            };

            var escolheu = await new Compactor(new ProviderQueConta("RESUMO: nenhum\nPENDENTE: nenhuma\nCOMBINADO:\n1"))
                .SummarizeAsync(0, turnos, default);
            escolheu.Combinado!.Select(f => f.Texto).Should().Equal("pode fazer dois grupos para eu ver como ficaria?");

            var semLinha = await new Compactor(new ProviderQueConta("RESUMO: nenhum\nPENDENTE: nenhuma"))
                .SummarizeAsync(0, turnos, default);
            semLinha.Combinado!.Select(f => f.Texto).Should().Equal("foi mal, ellen. te neguei de novo sem querer em 2 itens");
        }

        /// <summary>
        /// Caso real (teste do ato 5 no shadow): deduzidos do texto, os nomes saíam "Carlo" (a pasta
        /// do Windows), "Carlos" e "Ellen W.". Agora vêm das configurações; sem eles, "o usuário" e
        /// "o agente".
        /// </summary>
        [Fact]
        public async Task PromptDoCapitulo_UsaOsNomesDasConfiguracoes()
        {
            var provider = new ProviderQueConta("RESUMO: nenhum");
            await new Compactor(provider, usuario: "Carlos", agente: "Ellen").SummarizeAsync(0, new[] { TurnoCarimbado(0, "oi", "oi") }, default);

            provider.UltimoSistema.Should().Contain("Você escreve a memória de Ellen sobre um trecho da conversa com Carlos");
            Compactor.PromptDoCapitulo(null, null).Should().Contain("memória do agente sobre um trecho da conversa com o usuário");
        }

        /// <summary>
        /// Trecho só de execução: o resumidor escreve "nenhum" e o capítulo fica como era, sem as
        /// linhas novas gastando tokens.
        /// </summary>
        [Fact]
        public async Task ResumoNenhum_NaoEntraNoCapitulo()
        {
            var turnos = new[] { TurnoCarimbado(0, "crie o arquivo", "Criado.") };
            var capitulo = await new Compactor(new ProviderQueConta("RESUMO: nenhum\nFRASES: nenhuma\nOBJETIVO: criar o arquivo"))
                .SummarizeAsync(0, turnos, default);

            capitulo.Resumo.Should().BeNull();
            capitulo.Frases.Should().BeEmpty();
            capitulo.Render().Should().NotContain("Resumo:").And.NotContain("Frases memoráveis:");
        }

        /// <summary>
        /// O resumo passa pela mesma conferência das lições: a frase que cita um valor sem origem
        /// no trecho sai, e o resto do resumo fica.
        /// </summary>
        [Fact]
        public void Resumo_FraseComValorSemOrigem_Sai()
        {
            var turnos = new[] { TurnoCarimbado(0, "separa as lojas por estado", "Separei.") };
            var (resumo, _, _) = Compactor.LerResumoEFrases(
                "RESUMO: Carlos pediu para separar as lojas por estado. Ellen criou 27 grupos.\nOBJETIVO: separar",
                "separa as lojas por estado\nSeparei.", turnos);

            resumo.Should().Be("Carlos pediu para separar as lojas por estado.");
        }

        /// <summary>
        /// Caso real (shadow, capítulo 105): o objetivo reserva e a pendência do turno interrompido
        /// começavam por "[terça-feira - 27/10/2026 | 12:08]", o carimbo que o orbe põe na fala.
        /// </summary>
        [Fact]
        public void ObjetivoReservaEPendenciaInterrompida_SemCarimbo()
        {
            var turnos = new[]
            {
                TurnoCarimbado(0, "Ellen ficou perfeito! pode criar as demais", "[turno encerrado sem resposta: cancelado por você]")
            };

            Compactor.ObjetivoDaFala(turnos).Should().Be("pedido do turno 1: \"Ellen ficou perfeito! pode criar as demais\"");
            Pendencias.Extrair(turnos).Should().ContainSingle()
                .Which.Texto.Should().Contain("Pedido: \"Ellen ficou perfeito!").And.NotContain("27/10/2026");
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

        /// <summary>
        /// Caso real (teste do ato 5 no shadow): o ato só via objetivo e lições dos capítulos e
        /// perdia a história do arco; sem o registro do código, manteve como pendente um cadastro
        /// já feito; e, sem nenhuma dinâmica nos capítulos, inventou duas linhas vagas. Agora ele
        /// recebe o que cada capítulo escreveu e o registro do arco, escreve o resumo do arco, e o
        /// código só aceita frase que veio de um capítulo e dinâmica que cita trechos do arco.
        /// </summary>
        [Fact]
        public async Task Ato_ResumoDoArco_FrasesDosCapitulos_EDinamicaComTrechos()
        {
            Chapter Cap(int i, string resumo, string frase) => new(i, "", i, i, resumo, Array.Empty<Artifact>(),
                Versao: BlocoEstruturado.Versao, Resumo: resumo, Frases: new[] { frase },
                Combinado: new[] { new FalaCombinada(i, $"pedido {i}") }, Estado: EstadoDoTrecho.Vazio);
            var caps = new[]
            {
                Cap(0, "Carlos pediu um painel e Ellen montou o rascunho.", "Carlos: \"COMO DIABOS você sabe??\""),
                Cap(1, "Carlos corrigiu o nome e Ellen passou a usar Carlos.", "Ellen: \"anotado, Carlos com s!\"")
            };
            const string resposta = "RESUMO:\nCarlos pediu um painel e Ellen montou o rascunho. Depois ele corrigiu o nome.\n\n"
                                  + "FRASES:\n- Carlos: \"COMO DIABOS você sabe??\"\n- Ellen: \"frase que nenhum capítulo tem\"\n\n"
                                  + "DINÂMICA:\n- Ellen passou a chamar Carlos com s depois da correção (trechos 2)\n"
                                  + "- A relação ficou mais madura\n- Carlos passou a confiar mais (trechos 9)\n\n"
                                  + "APRENDIDO:\n- o painel parte do rascunho\nPENDENTE: nenhuma";
            var provider = new ProviderQueConta(resposta);

            var ato = await new Compactor(provider, usuario: "Carlos", agente: "Ellen").PromoteAsync(0, caps, default);

            provider.UltimoUsuario.Should().Contain("RESUMO: Carlos corrigiu o nome").And.Contain("--- REGISTRADO POR CÓDIGO NO ARCO ---")
                .And.Contain("pedido 1");
            ato.Resumo.Should().StartWith("Carlos pediu um painel");
            ato.Frases.Should().Equal("Carlos: \"COMO DIABOS você sabe??\"");
            ato.Dinamica.Should().Equal("Ellen passou a chamar Carlos com s depois da correção (trechos 2)");
            ato.Objetivo.Should().BeNull();
            ato.Render().Should().Contain("Dinâmica entre agente e usuário:").And.NotContain("Objetivo:");
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

            // 40, e não 20: pedido do usuário, capítulos maiores (35–50 mil tokens crus) para o
            // ato receber menos capítulos e mais ricos; com 20 turnos, o teto de tokens nunca chegava.
            s.TurnosPorCapitulo.Should().Be(40);
            s.CapitulosPorAto.Should().Be(2);
            s.EsconderResultadosDepoisDe.Should().Be(0);
            new UserAppSettings().MemoriaComModelo.Should().BeTrue();
        }
        private sealed class ProviderQueConta : IChatProvider
        {
            private readonly string _resposta;
            public ProviderQueConta(string resposta = "OBJETIVO: x") => _resposta = resposta;

            public int Chamadas { get; private set; }
            public string UltimoSistema { get; private set; } = "";
            public string UltimoUsuario { get; private set; } = "";
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
                UltimoSistema = m.Count > 0 ? string.Concat(m[0].Content.Select(p => p.Text)) : "";
                UltimoUsuario = m.Count > 1 ? string.Concat(m[^1].Content.Select(p => p.Text)) : "";
                return Task.FromResult(new ChatCompletionResult(_resposta, null, null));
            }

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
