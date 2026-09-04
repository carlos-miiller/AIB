using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using AIB.Services.Mail;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O vigia de e-mail — ideias_futuras/07, o funil, a rajada e o digest.
    /// <para>
    /// Tudo aqui é sobre o que acontece SEM ninguém olhando: o vigia roda com a janela fechada,
    /// e é a única parte do programa que vê conteúdo de e-mail. Os erros dele são caros porque
    /// são invisíveis — uma mensagem descartada por engano não aparece em lugar nenhum, e o
    /// produto inteiro se apoia no número de descartados ser confiável.
    /// </para>
    /// </summary>
    public class VigiaDeEmailTests
    {
        /// <summary>A fabrica montada pelo ultimo MontarCompleto. Serve so aos ensaios.</summary>
        private static FabricaFixa? UltimaFabrica;

        private static MensagemDeEmail Msg(
            uint uid = 1, string de = "ana@empresa.com", string assunto = "assunto",
            bool direto = true, bool importante = false, string[]? rotulos = null,
            DateTime? quando = null, string corpo = "")
            => new(uid, "thr", de, "Ana", assunto, quando ?? DateTime.UtcNow,
                   direto, importante, rotulos ?? Array.Empty<string>(), true, corpo);

        // ─────────────────────────────────────────────────────────────────
        // regras.md
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ORegrasMd_LE_AsFontesDeAlerta()
        {
            var regras = RegrasDoVigia.Interpretar(new[]
            {
                "# fontes de alerta",
                "firewall@empresa.com.br   → rajada a partir de 3 em 30 min",
                "nobreak@empresa.com.br    -> rajada a partir de 2 em 15 min"
            });

            regras.Fontes.Should().HaveCount(2);
            regras.Fonte("firewall@empresa.com.br")!.Minimo.Should().Be(3);
            regras.Fonte("firewall@empresa.com.br")!.Janela.Should().Be(TimeSpan.FromMinutes(30));
            regras.Fonte("NOBREAK@EMPRESA.COM.BR")!.Minimo.Should().Be(2);
        }

        [Fact]
        public void LinhaTORTA_NaoCALA_AsOutras()
        {
            // É um arquivo que o usuário edita à mão. Um erro de digitação numa linha não pode
            // fazer o vigia esquecer as fontes de alerta que estavam certas.
            var regras = RegrasDoVigia.Interpretar(new[]
            {
                "isto não é uma regra",
                "sem-arroba → rajada a partir de 3 em 30 min",
                "firewall@empresa.com.br → rajada a partir de 3 em 30 min",
                "outro@x.com → alguma outra coisa"
            });

            regras.Fontes.Should().HaveCount(1);
            regras.Fonte("firewall@empresa.com.br").Should().NotBeNull();
        }

        [Fact]
        public void JanelaEmHORAS_TambemVale()
        {
            RegrasDoVigia.Interpretar(new[] { "x@y.com → rajada a partir de 5 em 2 horas" })
                .Fonte("x@y.com")!.Janela.Should().Be(TimeSpan.FromHours(2));
        }

        // ─────────────────────────────────────────────────────────────────
        // Degraus 0 e 1
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OGmailJaClassificou_EIssoBASTA()
        {
            // Degrau 0: as categorias saem calculadas do servidor, de graça. Não há por que
            // reimplementar heurística de newsletter.
            FiltroDeTriagem.Avaliar(
                Msg(rotulos: new[] { "CATEGORY_PROMOTIONS" }, direto: false),
                RegrasDoVigia.Vazias).Sobe.Should().BeFalse();
        }

        [Fact]
        public void MarcadaComoImportante_SOBE_MesmoEmCopia()
        {
            FiltroDeTriagem.Avaliar(
                Msg(importante: true, direto: false, rotulos: new[] { "CATEGORY_UPDATES" }),
                RegrasDoVigia.Vazias).Sobe.Should().BeTrue("o Google já disse que importa");
        }

        [Fact]
        public void FonteDeAlerta_NUNCA_ECortadaPelaForma()
        {
            // O firewall manda de noreply@, automático e repetitivo — tudo o que os filtros
            // baratos matam. Foi a coisa mais urgente do dia 01/09.
            var regras = RegrasDoVigia.Interpretar(
                new[] { "firewall@empresa.com.br → rajada a partir de 3 em 30 min" });

            FiltroDeTriagem.Avaliar(
                Msg(de: "firewall@empresa.com.br", direto: false,
                    rotulos: new[] { "CATEGORY_UPDATES" }),
                regras).Sobe.Should().BeTrue();
        }

        [Fact]
        public void EmCopiaEDeRobo_CAI()
        {
            FiltroDeTriagem.Avaliar(Msg(de: "noreply@loja.com", direto: false), RegrasDoVigia.Vazias)
                .Sobe.Should().BeFalse();
        }

        [Fact]
        public void NaDUVIDA_SOBE()
        {
            // Regra 5 do vigia. Um e-mail chato subindo custa três segundos; um importante
            // sumindo custa o que já se perde hoje — e some sem aparecer em lugar nenhum.
            FiltroDeTriagem.Avaliar(Msg(de: "pessoa@parceiro.com", direto: false), RegrasDoVigia.Vazias)
                .Sobe.Should().BeTrue();
        }

        [Fact]
        public void DominioChamadoNoreply_NaoTornaTudoAutomatico()
        {
            // A checagem é da parte ANTES do arroba. Um domínio "noreply.com.br" não faz de
            // toda pessoa que escreve de lá um robô.
            FiltroDeTriagem.EhAutomatico("carlos@noreply.com.br").Should().BeFalse();
            FiltroDeTriagem.EhAutomatico("no-reply@empresa.com").Should().BeTrue();
        }

        // ─────────────────────────────────────────────────────────────────
        // Rajada
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void DozeEmQuarentaMinutos_EUmIncidente()
        {
            var regras = RegrasDoVigia.Interpretar(
                new[] { "firewall@empresa.com.br → rajada a partir de 3 em 30 min" });

            var inicio = new DateTime(2026, 9, 4, 9, 14, 0, DateTimeKind.Utc);
            var alertas = Enumerable.Range(0, 12)
                .Select(i => Msg(uid: (uint)i, de: "firewall@empresa.com.br",
                                 assunto: $"WAN2 perda {i}", quando: inicio.AddMinutes(i * 2)))
                .ToList();

            var rajadas = DetectorDeRajada.Encontrar(alertas, regras);

            rajadas.Should().HaveCount(1);
            rajadas[0].Quantas.Should().BeGreaterThanOrEqualTo(3);
            rajadas[0].Assuntos.Should().NotBeEmpty("o modelo escreve por cima destes fatos");
        }

        [Fact]
        public void AsMESMAS_Espalhadas_NaoSaoRajada()
        {
            // O sinal não está em nenhuma mensagem: está no volume E no ritmo. Seis alertas em
            // seis horas é a vida normal de um firewall.
            var regras = RegrasDoVigia.Interpretar(
                new[] { "firewall@empresa.com.br → rajada a partir de 3 em 30 min" });

            var inicio = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
            var espalhados = Enumerable.Range(0, 6)
                .Select(i => Msg(uid: (uint)i, de: "firewall@empresa.com.br",
                                 quando: inicio.AddHours(i)))
                .ToList();

            DetectorDeRajada.Encontrar(espalhados, regras).Should().BeEmpty();
        }

        [Fact]
        public void RemetenteNaoDECLARADO_NaoViraRajada()
        {
            // Quem decide o que é fonte de alerta é o usuário, no regras.md. Sem isso, uma
            // newsletter diária viraria incidente.
            var muitas = Enumerable.Range(0, 20)
                .Select(i => Msg(uid: (uint)i, de: "news@site.com",
                                 quando: DateTime.UtcNow.AddMinutes(i)))
                .ToList();

            DetectorDeRajada.Encontrar(muitas, RegrasDoVigia.Vazias).Should().BeEmpty();
        }

        // ─────────────────────────────────────────────────────────────────
        // O que o modelo devolve
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void JSON_LimpoEInterpretado()
        {
            var v = TriadorDeEmail.Interpretar(
                """[{"uid":91043,"urgencia":"maxima","resumo":"assinar o aditivo até 18h"}]""");

            v.Should().HaveCount(1);
            v[0].Uid.Should().Be(91043);
            v[0].Urgencia.Should().Be(MailUrgency.Maxima);
            v[0].Resumo.Should().Contain("aditivo");
        }

        [Fact]
        public void CercaDeCodigo_EFraseDeApresentacao_NaoAtrapalham()
        {
            // O modelo faz isso o tempo todo, por mais claro que o prompt seja.
            var v = TriadorDeEmail.Interpretar(
                "Claro! Aqui está:\n```json\n[{\"uid\":\"7\",\"urgencia\":\"MÉDIA\",\"resumo\":\"responder\"}]\n```");

            v.Should().HaveCount(1);
            v[0].Uid.Should().Be(7, "uid como texto ainda é um uid");
            v[0].Urgencia.Should().Be(MailUrgency.Media);
        }

        [Fact]
        public void UrgenciaINVENTADA_ViraMEDIA_NaoBaixa()
        {
            // Regra 5 de novo: na dúvida, sinaliza. Cair para baixa esconderia a mensagem no
            // fim da lista por causa de uma palavra que o modelo escolheu mal.
            TriadorDeEmail.Nivel("crítica-total").Should().Be(MailUrgency.Media);
            TriadorDeEmail.Nivel("").Should().Be(MailUrgency.Media);
            TriadorDeEmail.Nivel("urgente").Should().Be(MailUrgency.Maxima);
            TriadorDeEmail.Nivel("informativo").Should().Be(MailUrgency.Baixa);
        }

        [Fact]
        public void RespostaIMPRESTAVEL_DevolveVazio_NaoChuta()
        {
            TriadorDeEmail.Interpretar("desculpe, não consegui").Should().BeEmpty();
            TriadorDeEmail.Interpretar("[isto não é json").Should().BeEmpty();
            TriadorDeEmail.Interpretar(null).Should().BeEmpty();
        }

        [Fact]
        public void OPromptNaoLEVA_MaisDoQuePrecisa()
        {
            // O corpo desce truncado, e nada além do que o modelo precisa para decidir. Um lote
            // com trinta mensagens inteiras passaria de vinte mil tokens de prefill.
            string texto = TriadorDeEmail.Montar(new[]
            {
                Msg(uid: 5, assunto: "Contrato", corpo: "precisa da sua assinatura")
            });

            texto.Should().Contain("[5]").And.Contain("Contrato").And.Contain("assinatura");
        }

        [Fact]
        public void OCorpoDESCE_Truncado()
        {
            string longo = new string('x', 5000);
            string curto = MensagemDeEmail.Encurtar(longo);

            curto.Length.Should().BeLessThan(longo.Length);
            curto.Should().EndWith("…");
        }

        [Fact]
        public void HTML_PerdeAsTags_AntesDeIrAoModelo()
        {
            MailKitMailService.SemMarcacao("<p>oi <b>Carlo</b></p>")
                .Should().Be("oi Carlo");
        }

        // ─────────────────────────────────────────────────────────────────
        // Agenda
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void AntesDoPrimeiroHorario_NaoRodaNada()
        {
            // Às 7h da manhã não há digest atrasado a recuperar: o dia não começou, e disparar
            // agora mostraria a caixa de ontem como novidade.
            AgendaDoVigia.HoraDoDigest(new DateTime(2026, 9, 4, 7, 0, 0), null)
                .Should().BeFalse();
        }

        [Fact]
        public void LigarOComputadorTARDE_NaoPERDE_ODigestDaManha()
        {
            // A comparação é "passou do horário e ainda não rodou depois dele", não "o relógio
            // marca exatamente 8:25". Máquina suspensa, desligada ou um minuto perdido pelo
            // laço não podem custar o dia inteiro.
            AgendaDoVigia.HoraDoDigest(new DateTime(2026, 9, 4, 10, 30, 0), null)
                .Should().BeTrue();
        }

        [Fact]
        public void JaRODOU_DepoisDoHorario_NaoRepete()
        {
            var agora = new DateTime(2026, 9, 4, 10, 30, 0);
            var rodou = new DateTime(2026, 9, 4, 9, 0, 0);

            AgendaDoVigia.HoraDoDigest(agora, rodou).Should().BeFalse();
        }

        [Fact]
        public void OHorarioSEGUINTE_DISPARA_DeNovo()
        {
            var rodouDeManha = new DateTime(2026, 9, 4, 8, 30, 0);

            AgendaDoVigia.HoraDoDigest(new DateTime(2026, 9, 4, 12, 56, 0), rodouDeManha)
                .Should().BeTrue();
        }

        [Fact]
        public void ASondagemRespeita_OIntervalo()
        {
            var agora = new DateTime(2026, 9, 4, 10, 0, 0);

            AgendaDoVigia.HoraDaSondagem(agora, agora.AddMinutes(-5)).Should().BeFalse();
            AgendaDoVigia.HoraDaSondagem(agora, agora.AddMinutes(-21)).Should().BeTrue();
            AgendaDoVigia.HoraDaSondagem(agora, null).Should().BeTrue();
        }

        // ─────────────────────────────────────────────────────────────────
        // O laço inteiro
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ODigest_LE_TRIA_ENTREGA()
        {
            var (vigia, _) = Montar(
                lidas: new[]
                {
                    Msg(uid: 1, assunto: "Contrato Vertex", corpo: "assinar até 18h"),
                    Msg(uid: 2, de: "noreply@loja.com", assunto: "Promoção", direto: false)
                },
                resposta: """[{"uid":1,"urgencia":"maxima","resumo":"assinar o aditivo até 18h"}]""");

            var digesto = await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            digesto.Lidas.Should().Be(2);
            digesto.Itens.Should().HaveCount(1);
            digesto.Itens[0].Urgency.Should().Be(MailUrgency.Maxima);
            digesto.Descartadas.Should().HaveCount(1, "a promoção caiu, e o porquê fica registrado");
            digesto.Descartadas[0].Motivo.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task MensagemSEM_Veredito_NaoSOME_DaTela()
        {
            // O modelo esquecer uma linha do JSON não pode ser o mesmo que a mensagem não
            // existir: é exatamente o falso negativo invisível que a regra 5 proíbe.
            var (vigia, _) = Montar(
                lidas: new[] { Msg(uid: 1, assunto: "A"), Msg(uid: 2, assunto: "B") },
                resposta: """[{"uid":1,"urgencia":"baixa","resumo":"nada urgente"}]""");

            var digesto = await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            // A "A" saiu da tela porque o modelo disse baixa; a "B", que ele esqueceu, entra
            // com o resumo de codigo e urgencia media. Esquecer uma linha do JSON nao pode
            // equivaler a mensagem nao existir.
            digesto.Itens.Should().ContainSingle(i => i.Name == "B");
        }

        [Fact]
        public async Task ModeloFORA_DoAr_NaoAPAGA_ACaixaDaTela()
        {
            var (vigia, _) = Montar(
                lidas: new[] { Msg(uid: 1, assunto: "Contrato") },
                resposta: null,
                modeloQuebra: true);

            var digesto = await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            digesto.Itens.Should().HaveCount(1, "sem modelo, ainda dá para dizer o que chegou");
            digesto.Itens[0].Urgency.Should().Be(MailUrgency.Media);
        }

        [Fact]
        public async Task ASondagem_NaoACORDA_OModelo()
        {
            // No estado estável ela não encontra nada, e é por isso que ela é barata. Acordar o
            // 9B de vinte em vinte minutos deixaria a máquina ocupada o dia inteiro à toa.
            var (vigia, provider) = Montar(
                lidas: new[] { Msg(uid: 1, assunto: "Contrato") },
                resposta: """[{"uid":1,"urgencia":"maxima","resumo":"x"}]""");

            await vigia.ExecutarAsync(comModelo: false, CancellationToken.None);

            provider.Chamadas.Should().Be(0);
            vigia.DigestosFeitos.Should().Be(0);
        }

        [Fact]
        public async Task ChaveDESLIGADA_NaoLE_Nada()
        {
            // A triagem é opt-in. Ninguém ganha um programa lendo o próprio e-mail por ter
            // atualizado a versão.
            var (vigia, provider, email) = MontarCompleto(
                triagemLigada: false,
                lidas: new[] { Msg(uid: 1) },
                resposta: "[]");

            await vigia.BaterAsync();

            email.Chamadas.Should().Be(0);
            provider.Chamadas.Should().Be(0);
        }

        [Fact]
        public void AFraseDoShadow_SAI_DoCodigo_NaoDoModelo()
        {
            // Os números são exatos porque não passaram pelo modelo. Se o 9B alucinar, alucina
            // no resumo de uma mensagem, não na contagem que abre o aviso.
            var digesto = new DigestoDeEmail(
                new[]
                {
                    new MailSummary("a", "b", MailUrgency.Maxima),
                    new MailSummary("c", "d", MailUrgency.Baixa)
                },
                Lidas: 34, Array.Empty<Descartada>(), Array.Empty<Rajada>(), DateTime.UtcNow);

            digesto.Frase().Should().Contain("34").And.Contain("1 precisa");
        }

        [Fact]
        public void ComRAJADA_AFraseFALA_DoIncidente()
        {
            var digesto = new DigestoDeEmail(
                Array.Empty<MailSummary>(), 12, Array.Empty<Descartada>(),
                new[]
                {
                    new Rajada("firewall@x.com", "Firewall", 12,
                               new DateTime(2026, 9, 4, 9, 14, 0, DateTimeKind.Utc),
                               new DateTime(2026, 9, 4, 10, 1, 0, DateTimeKind.Utc),
                               Array.Empty<string>())
                },
                DateTime.UtcNow);

            digesto.Frase().Should().Contain("12 alertas").And.Contain("Firewall");
        }

        // ─────────────────────────────────────────────────────────────────
        // Conversas vigiadas
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ResponderUmaConversa_ACOLOCA_SobVigia()
        {
            // Quem responde geralmente espera retorno. Detectar isso e IMAP puro — mensagem na
            // pasta de enviados com a mesma X-GM-THRID —, e por isso nao passa por modelo
            // nenhum: a decisao de interromper fica inteira na parte que nao alucina.
            var agora = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

            var vigias = VigiasDoEmail.Atualizar(
                Array.Empty<VigiaDeThread>(),
                new[] { new ThreadRespondida("17ab", agora.AddHours(-2)) },
                agora);

            vigias.Should().HaveCount(1);
            vigias[0].Thrid.Should().Be("17ab");
            vigias[0].Ate.Should().BeAfter(agora);
            vigias[0].Porque.Should().NotBeNullOrWhiteSpace("o arquivo tem de ser auditavel");
        }

        [Fact]
        public void VigiaVENCIDA_MORRE_Sozinha()
        {
            // Sem prazo a lista so cresce, e em um mes toda a caixa estaria vigiada — que e o
            // mesmo que nenhuma estar.
            var agora = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

            var velha = new VigiaDeThread
            {
                Thrid = "antiga",
                Ate = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)
            };

            VigiasDoEmail.Atualizar(new[] { velha }, Array.Empty<ThreadRespondida>(), agora)
                .Should().BeEmpty();
        }

        [Fact]
        public void ResponderDeNOVO_RENOVA_EmVezDeDuplicar()
        {
            var agora = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

            var antiga = new VigiaDeThread
            {
                Thrid = "17ab",
                RespondidaEm = agora.AddDays(-5),
                Ate = agora.AddDays(2)
            };

            var vigias = VigiasDoEmail.Atualizar(
                new[] { antiga },
                new[] { new ThreadRespondida("17ab", agora) },
                agora);

            vigias.Should().HaveCount(1, "continuar respondendo e continuar esperando retorno");
            vigias[0].Ate.Should().BeAfter(antiga.Ate);
        }

        [Fact]
        public void RespostaNumaConversaVIGIADA_SOBE_ContraTudo()
        {
            // Ele MESMO puxou aquele assunto. Um filtro que descartasse a resposta estaria
            // descartando justamente o que ele foi buscar.
            var vigiadas = new HashSet<string> { "17ab" };

            var msg = new MensagemDeEmail(
                1, "17ab", "noreply@sistema.com", "Sistema", "Re: chamado",
                DateTime.UtcNow, Direto: false, Importante: false,
                new[] { "CATEGORY_UPDATES" }, true, "");

            FiltroDeTriagem.Avaliar(msg, RegrasDoVigia.Vazias, vigiadas)
                .Sobe.Should().BeTrue();
        }

        [Fact]
        public void OVigiasJson_VAI_EVOLTA_DoDisco()
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-vigias-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);
            var arquivo = new VigiasDoEmail(pasta);

            arquivo.Gravar(new[]
            {
                new VigiaDeThread
                {
                    Thrid = "17ab", Porque = "voce respondeu",
                    Ate = new DateTime(2026, 9, 11), Acorda9b = true
                }
            });

            var lidas = arquivo.Ler();
            lidas.Should().HaveCount(1);
            lidas[0].Thrid.Should().Be("17ab");
            lidas[0].Acorda9b.Should().BeTrue();
        }

        [Fact]
        public void SondagemACORDA_OModelo_SoQuandoAVigiaMANDA()
        {
            // O campo acorda9b existe para essa decisao ser um DADO conferivel no arquivo, e
            // nao uma frase que o modelo interpretaria de um jeito hoje e de outro amanha.
            var msg = new MensagemDeEmail(1, "17ab", "x@y.com", "X", "Re:", DateTime.UtcNow,
                                          true, false, Array.Empty<string>(), true, "");

            var calada = new VigiaDeThread { Thrid = "17ab", Acorda9b = false };
            var barulhenta = new VigiaDeThread { Thrid = "17ab", Acorda9b = true };

            MailDigestService.Urgente(new[] { calada }, new[] { msg }, Array.Empty<Rajada>())
                .Should().BeFalse("responder nao e emergencia");

            MailDigestService.Urgente(new[] { barulhenta }, new[] { msg }, Array.Empty<Rajada>())
                .Should().BeTrue();
        }

        [Fact]
        public void RAJADA_SempreACORDA_ASondagem()
        {
            var rajada = new Rajada("firewall@x.com", "Firewall", 12,
                                    DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow,
                                    Array.Empty<string>());

            MailDigestService.Urgente(
                Array.Empty<VigiaDeThread>(), Array.Empty<MensagemDeEmail>(), new[] { rajada })
                .Should().BeTrue("um incidente as 9h14 no digest das 12h55 nao vale nada");
        }

        [Fact]
        public async Task ODigest_GRAVA_AsConversasRespondidas()
        {
            var (vigia, _, email, pasta) = MontarComPasta(
                lidas: new[] { Msg(uid: 1, assunto: "Contrato") },
                resposta: "[{\"uid\":1,\"urgencia\":\"media\",\"resumo\":\"responder\"}]");

            email.Respondidas = new[] { new ThreadRespondida("17ab", DateTime.UtcNow.AddDays(-1)) };

            await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            new VigiasDoEmail(pasta).Ler()
                .Should().ContainSingle(v => v.Thrid == "17ab");
        }

        // ─────────────────────────────────────────────────────────────────
        // Defeitos vistos em producao (04/09/2026)
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void CONTAR_NaoCONSOME_OQueFaltaTriar()
        {
            // Defeito real: os dois progressos dividiam o mesmo campo. A tela de configuracoes
            // contava as mensagens, avancava o marcador, e o vigia — rodando depois —
            // encontrava a caixa "em dia". Setecentas e dez por triar viraram uma.
            var depoisDaContagem = new EstadoDaCaixa
            {
                UidValidity = 651454841,
                LastUid = 27711,        // a TELA contou ate aqui
                LastTriagedUid = 0      // o VIGIA nao triou nada ainda
            };

            depoisDaContagem.ServeParaPartir(651454841).Should().BeTrue("a contagem sabe onde parou");
            depoisDaContagem.ServeParaTriar(651454841).Should().BeFalse(
                "a triagem nao pode herdar o progresso de quem so contou");
        }

        [Fact]
        public async Task ODigest_GRAVA_OProgressoDaTriagem_SemMexerNoDaContagem()
        {
            var (vigia, _, email, pasta) = MontarComPasta(
                lidas: new[] { Msg(uid: 500, assunto: "Contrato") },
                resposta: "[{\"uid\":500,\"urgencia\":\"media\",\"resumo\":\"responder\"}]");

            var estado = new EstadoDasCaixas(pasta);
            estado.Gravar("eu@empresa.com", new EstadoDaCaixa
            {
                UidValidity = 1, LastUid = 9999, LastTriagedUid = 0
            });

            await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            var depois = estado.Ler("eu@empresa.com")!;
            depois.LastTriagedUid.Should().Be(500, "o vigia marcou ate onde triou");
            depois.LastUid.Should().Be(9999, "e nao mexeu no marcador da tela");
        }

        [Fact]
        public void MarketingENDERECADO_AVoce_CONTINUA_Marketing()
        {
            // Defeito real: um e-mail da Wellhub, de no-reply@, endereçado diretamente ao
            // usuario, subiu porque "endereçada a voce" era checado ANTES de "remetente
            // automatico" — e o modelo ainda o classificou como MAXIMA. Ser destinatario de um
            // robo nao e ser destinatario de um pedido.
            var promo = new MensagemDeEmail(
                27711, "thr", "no-reply@mail.wellhub.com", "Wellhub",
                "Ganhe R$75 de desconto", DateTime.UtcNow,
                Direto: true, Importante: false, Array.Empty<string>(), true, "indique e ganhe");

            FiltroDeTriagem.Avaliar(promo, RegrasDoVigia.Vazias).Sobe.Should().BeFalse();
        }

        [Fact]
        public void RoboIMPORTANTE_OuVIGIADO_ContinuaSUBINDO()
        {
            // As portas de fuga que impedem a correcao acima de calar o alerta do firewall e o
            // aviso do banco.
            var doBanco = new MensagemDeEmail(
                1, "thr", "noreply@banco.com", "Banco", "Transacao suspeita", DateTime.UtcNow,
                Direto: true, Importante: true, Array.Empty<string>(), true, "");

            FiltroDeTriagem.Avaliar(doBanco, RegrasDoVigia.Vazias).Sobe.Should().BeTrue(
                "o Gmail marcou como importante");

            var resposta = new MensagemDeEmail(
                2, "17ab", "noreply@sistema.com", "Sistema", "Re: chamado", DateTime.UtcNow,
                Direto: true, Importante: false, Array.Empty<string>(), true, "");

            FiltroDeTriagem.Avaliar(resposta, RegrasDoVigia.Vazias,
                                    new HashSet<string> { "17ab" })
                .Sobe.Should().BeTrue("e uma conversa que voce comecou");
        }

        [Fact]
        public async Task ATriagem_USA_OModeloPRINCIPAL_NaoODoShadow()
        {
            // Defeito real: com um 0.8b no campo "Modelo do Shadow", a triagem classificou um
            // cupom de marketing como MAXIMA e escreveu um resumo que nao estava em lugar
            // nenhum da mensagem. O documento e explicito: o degrau 3 e do 9B, e modelo pequeno
            // nunca decide o que sobe — ele e confiante ate quando erra.
            var (vigia, _, _, _) = MontarComPasta(
                lidas: new[] { Msg(uid: 1, assunto: "Contrato") },
                resposta: "[{\"uid\":1,\"urgencia\":\"media\",\"resumo\":\"x\"}]");

            await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            UltimaFabrica!.Ultimas!.ModelName.Should().Be("qwen3.5:9b");
        }

        [Fact]
        public void OMarcoDoDigest_SOBREVIVE_AoFechamentoDoPrograma()
        {
            // Defeito real: o marcador vivia em memoria, entao TODO arranque depois das 8h25
            // disparava um digest. Abrir e fechar a AIB tres vezes numa manha custava tres
            // leituras da caixa e tres chamadas ao modelo, dizendo a mesma coisa.
            string pasta = Path.Combine(Path.GetTempPath(), "aib-marco-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);

            var quando = new DateTime(2026, 9, 4, 13, 0, 0, DateTimeKind.Local);
            new MarcoDoVigia(pasta).GravarDigest(quando);

            var depoisDeReabrir = new MarcoDoVigia(pasta).UltimoDigest;

            depoisDeReabrir.Should().NotBeNull();
            AgendaDoVigia.HoraDoDigest(new DateTime(2026, 9, 4, 13, 5, 0), depoisDeReabrir)
                .Should().BeFalse("o digest das 12h55 ja rodou, mesmo com o programa reiniciado");
        }

        // ─────────────────────────────────────────────────────────────────
        // A limpeza do corpo (medida na caixa real, 04/09)
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void FolhaDeEstilo_NaoVAI_ParaOModelo()
        {
            // Corpo real do log: o e-mail do Patreon e CSS do comeco ao fim. Vinte e uma
            // mensagens assim viraram 11.223 tokens e ONZE MINUTOS de prefill.
            string patreon = "Asaba #outlook a{padding: 0;} .ReadMsgBody{width: 100%;} " +
                             ".ExternalClass{width: 100%;} body{margin: 0; padding: 0;}";

            string limpo = MensagemDeEmail.Limpar(patreon);

            // Sobram palavras soltas de seletor ("a", "body") — inofensivas e curtas. O que
            // nao pode sobrar e a folha de estilo, que e o que ocupava o prompt.
            limpo.Should().StartWith("Asaba", "a frase de verdade sobrevive");
            limpo.Should().NotContain("ReadMsgBody").And.NotContain("padding")
                 .And.NotContain("width").And.NotContain("{");
            limpo.Length.Should().BeLessThan(patreon.Length / 3);
        }

        [Fact]
        public void ParedeDeURL_NaoVAI_ParaOModelo()
        {
            // O e-mail da Localiza traz quatro links de rastreamento de ~300 caracteres cada.
            // Nenhum deles diz nada ao modelo.
            string localiza = "Ola, Carlos https://click.e.localiza.com/?qs=ABB7InYiOjEsImQiOjQ5" +
                              "ODh9AAcAAAAABh_hLoy1lzqU8_wKXf9aJvMiF3uGyMASRQjrqpy9 " +
                              "Tem economia na pista.";

            string limpo = MensagemDeEmail.Limpar(localiza);

            limpo.Should().NotContain("http");
            limpo.Should().Contain("Ola, Carlos").And.Contain("Tem economia na pista.");
        }

        [Fact]
        public void InvisiveisDeDisparador_SOMEM()
        {
            // O Growth Supplements enche a mensagem de U+034F e hifen suave para esticar a
            // pre-visualizacao na caixa de entrada. Sao centenas deles, e viram tokens.
            string sujo = "Ofertas͏͏͏­­​ do mes";

            MensagemDeEmail.Limpar(sujo).Should().Be("Ofertas do mes");
        }

        [Fact]
        public void EntidadesHTML_VIRAM_Texto()
        {
            MensagemDeEmail.Limpar("Pix&nbsp;no&nbsp;Cr&#233;dito&zwnj;")
                .Should().Contain("Pix no Cr");
        }

        [Fact]
        public void OTetoCONTA_DepoisDaLimpeza()
        {
            // Antes o teto de 1200 era gasto com folha de estilo. Agora sao 600 caracteres de
            // frase — menos caracteres, muito mais conteudo.
            string frase = "Confirme sua presenca na reuniao de quinta.";
            string css = frase + " " + string.Concat(System.Linq.Enumerable.Repeat(
                ".classe" + " {padding: 0; margin: 13px 0; line-height: 100%;} ", 60));

            string curto = MensagemDeEmail.Encurtar(css);

            curto.Should().StartWith(frase);
            curto.Length.Should().BeLessThan(css.Length / 4);
        }

        // ─────────────────────────────────────────────────────────────────
        // O reconhecedor de remetente automatico
        // ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("messages-noreply@linkedin.com")]
        [InlineData("updates-noreply@linkedin.com")]
        [InlineData("newsletter@nuuvem.com")]
        [InlineData("marketing@digitra.com")]
        public void MarcaNoMEIO_DoEndereco_TambemCONTA(string endereco)
        {
            // Visto em producao: a checagem era StartsWith, entao "messages-noreply" passava
            // batido porque comeca com "messages". A marca estava la, no meio.
            FiltroDeTriagem.EhAutomatico(endereco).Should().BeTrue();
        }

        [Theory]
        [InlineData("picpay@marketing.picpay.com")]   // a marca esta no DOMINIO, nao na parte local
        [InlineData("carlos@noreply.com.br")]
        [InlineData("ana@empresa.com")]
        [InlineData("joao.silva@cliente.com.br")]
        public void PessoaCONTINUA_Pessoa(string endereco)
        {
            FiltroDeTriagem.EhAutomatico(endereco).Should().BeFalse();
        }

        // ─────────────────────────────────────────────────────────────────
        // O que chega a tela
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task BAIXA_NaoVAI_ParaATela_MasCONTINUA_Auditavel()
        {
            // Visto em producao: de 21 triadas o modelo marcou 20 como baixa, acertou, e as 20
            // foram para o painel assim mesmo. Fazer o trabalho e devolver a pilha inteira nao
            // e triar.
            var (vigia, _, _, _) = MontarComPasta(
                lidas: new[]
                {
                    Msg(uid: 1, assunto: "Contrato"),
                    Msg(uid: 2, assunto: "Newsletter"),
                    Msg(uid: 3, assunto: "Promo")
                },
                resposta: "[{\"uid\":1,\"urgencia\":\"maxima\",\"resumo\":\"assinar hoje\"}," +
                          "{\"uid\":2,\"urgencia\":\"baixa\",\"resumo\":\"ler depois\"}," +
                          "{\"uid\":3,\"urgencia\":\"baixa\",\"resumo\":\"ignorar\"}]");

            var digesto = await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            digesto.Itens.Should().HaveCount(1, "so o que pede alguma coisa");
            digesto.Itens[0].Urgency.Should().Be(MailUrgency.Maxima);
            digesto.Lidas.Should().Be(3, "a contagem continua inteira");
            digesto.Descartadas.Should().Contain(d => d.Motivo.Contains("nao pedem nada")
                                                   || d.Motivo.Contains("não pedem nada"));
        }

        // ─────────────────────────────────────────────────────────────────
        // Andaimes
        // ─────────────────────────────────────────────────────────────────

        private static (MailDigestService, ProviderFalso) Montar(
            IReadOnlyList<MensagemDeEmail> lidas, string? resposta, bool modeloQuebra = false)
        {
            var (vigia, provider, _) = MontarCompleto(true, lidas, resposta, modeloQuebra);
            return (vigia, provider);
        }

        private static (MailDigestService, ProviderFalso, EmailFalso, string) MontarComPasta(
            IReadOnlyList<MensagemDeEmail> lidas, string? resposta, bool modeloQuebra = false)
        {
            var (v, prov, mail) = MontarCompleto(true, lidas, resposta, modeloQuebra, out string pasta);
            return (v, prov, mail, pasta);
        }

        private static (MailDigestService, ProviderFalso, EmailFalso) MontarCompleto(
            bool triagemLigada, IReadOnlyList<MensagemDeEmail> lidas, string? resposta,
            bool modeloQuebra = false)
            => MontarCompleto(triagemLigada, lidas, resposta, modeloQuebra, out _);

        private static (MailDigestService, ProviderFalso, EmailFalso) MontarCompleto(
            bool triagemLigada, IReadOnlyList<MensagemDeEmail> lidas, string? resposta,
            bool modeloQuebra, out string pastaUsada)
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-vigia-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);
            pastaUsada = pasta;

            var settings = new SettingsService(Path.Combine(pasta, "settings.json"));
            var config = settings.LoadSettings();
            config.ShadowHandlesMail = triagemLigada;

            // O par que reproduz o defeito de producao: um 9B na conversa e um 0.8b no campo
            // do Shadow. A triagem tem de escolher o primeiro.
            config.ModelName = "qwen3.5:9b";
            config.ShadowModelName = "qwen3.5:0.8b";
            config.MailAccounts = new List<MailAccountSettings>
            {
                new() { Address = "eu@empresa.com", ImapHost = "imap.gmail.com", ImapPort = 993, IsPrimary = true }
            };
            settings.SaveSettings(config);

            var cofre = new MailVault(pasta);
            cofre.Guardar("eu@empresa.com", "abcdefghijklmnop");

            var email = new EmailFalso(lidas);
            var provider = new ProviderFalso(resposta, modeloQuebra);
            UltimaFabrica = new FabricaFixa(provider);

            var vigia = new MailDigestService(
                settings, email, UltimaFabrica,
                cofre, new EstadoDasCaixas(pasta),
                agora: () => new DateTime(2026, 9, 4, 13, 0, 0),
                caminhoDasRegras: Path.Combine(pasta, "regras.md"),
                vigias: new VigiasDoEmail(pasta),
                raizDeDados: pasta);

            return (vigia, provider, email);
        }

        private sealed class EmailFalso : IMailService
        {
            private readonly IReadOnlyList<MensagemDeEmail> _lidas;
            public EmailFalso(IReadOnlyList<MensagemDeEmail> lidas) => _lidas = lidas;

            public int Chamadas { get; private set; }
            public bool Disponivel => true;
            public string MotivoDaIndisponibilidade => "";

            public Task<MailLoginResult> TestLoginAsync(string e, string s, CancellationToken ct) =>
                Task.FromResult(new MailLoginResult(true, default, "", true));

            public Task<MailScanResult> VarrerAsync(
                string e, string s, ImapEndpoint ep, DateTime d, EstadoDaCaixa? g, CancellationToken ct) =>
                Task.FromResult(new MailScanResult(true, 0, 0, false, 1, 0, ""));

            public Task<LeituraDaCaixa> LerAsync(
                string e, string s, ImapEndpoint ep, DateTime d, EstadoDaCaixa? g,
                string eu, CancellationToken ct)
            {
                Chamadas++;
                return Task.FromResult(new LeituraDaCaixa(_lidas, UidValidity));
            }

            /// <summary>Selo da caixa, para o ensaio poder simular renumeracao.</summary>
            public uint UidValidity { get; set; } = 1;

            /// <summary>As conversas em que o usuario escreveu, ditadas pelo ensaio.</summary>
            public IReadOnlyList<ThreadRespondida> Respondidas { get; set; } =
                Array.Empty<ThreadRespondida>();

            public Task<IReadOnlyList<ThreadRespondida>> ThreadsRespondidasAsync(
                string e, string s, ImapEndpoint ep, DateTime d, CancellationToken ct) =>
                Task.FromResult(Respondidas);
        }

        private sealed class ProviderFalso : IChatProvider
        {
            private readonly string? _resposta;
            private readonly bool _quebra;

            public ProviderFalso(string? resposta, bool quebra)
            {
                _resposta = resposta;
                _quebra = quebra;
            }

            public int Chamadas { get; private set; }
            public string Name => "falso";
            public string Model => "modelo-de-teste";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t, ChatRequestOptions o,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield break;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t,
                ChatRequestOptions o, CancellationToken ct)
            {
                Chamadas++;
                if (_quebra) throw new InvalidOperationException("provedor fora do ar");
                return Task.FromResult(new ChatCompletionResult(_resposta ?? "", null, null));
            }

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class FabricaFixa : IChatProviderFactory
        {
            private readonly IChatProvider _provider;
            public FabricaFixa(IChatProvider provider) => _provider = provider;

            /// <summary>As configuracoes da ultima vez que alguem pediu um provider.</summary>
            public UserAppSettings? Ultimas { get; private set; }

            public IChatProvider GetProvider(UserAppSettings settings)
            {
                Ultimas = settings;
                return _provider;
            }
        }
    }
}
