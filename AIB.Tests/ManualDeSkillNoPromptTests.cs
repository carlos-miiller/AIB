using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O manual da habilidade chegando ao modelo.
    /// <para>
    /// O corpo do SKILL.md era lido do disco e guardado em <c>LocalSkill.Instructions</c>, com o
    /// comentário "instruções de uso, para o modelo ler" — e nunca entregue a lugar nenhum. O
    /// prompt levava só a linha de descrição.
    /// </para>
    /// <para>
    /// Medido em 10/09: a skill do Bitrix é descrita apenas como "Skill oficial para o Bitrix24.
    /// Use para tarefas, leads e contatos." O modelo inventou <c>-Action "list_users"
    /// -Filter ""</c> — três invenções, três erradas. Os comandos são <c>get_task</c>,
    /// <c>list_tasks</c> e <c>call</c>; os parâmetros são <c>-Command</c>, <c>-Args</c> e
    /// <c>-Url</c>; e o SKILL.md dizia isso em letras garrafais. Não foi alucinação: foi o
    /// manual ficando na gaveta.
    /// </para>
    /// </summary>
    public class ManualDeSkillNoPromptTests
    {
        private static LocalSkill Skill(string instrucoes) => new()
        {
            Name = "bitrix",
            Description = "Skill oficial para o Bitrix24. Use para tarefas, leads e contatos.",
            Instructions = instrucoes
        };

        [Fact]
        public void OCorpoDoSkillMd_VAI_ParaOPrompt()
        {
            string texto = ConversationService.ManualDaSkill(Skill(
                "## Comandos Disponíveis\n"
                + "1. get_task <ID> --url <URI>\n"
                + "2. list_tasks --url <URI>\n"
                + "**IMPORTANTE**: sempre recupere a URI do webhook."));

            texto.Should().Contain("bitrix:", "a linha de descrição continua sendo a primeira");
            texto.Should().Contain("get_task");
            texto.Should().Contain("list_tasks");
            texto.Should().Contain("--url", "é o argumento obrigatório que o modelo não passou");
        }

        [Fact]
        public void OManual_VaiINDENTADO_SobASkillDele()
        {
            // Sem recuo, o modelo lê o manual de uma habilidade como se valesse para a lista
            // inteira — e chama a segunda com os argumentos da primeira.
            string texto = ConversationService.ManualDaSkill(Skill("get_task <ID>"));

            texto.Should().Contain("\n  get_task <ID>");
        }

        [Fact]
        public void SemManual_ALinha_ContinuaSendoUmaSo()
        {
            // Habilidade sem corpo não ganha bloco vazio nem linha em branco no prompt.
            string texto = ConversationService.ManualDaSkill(Skill(""));

            texto.Should().Be("- bitrix: Skill oficial para o Bitrix24. Use para tarefas, leads e contatos.\n");
        }

        [Fact]
        public void ManualGIGANTE_EhCortado()
        {
            // O prompt é prefixo cacheado, mas um SKILL.md de dez páginas ainda comeria a
            // janela. O começo é onde mora a forma de chamar.
            string texto = ConversationService.ManualDaSkill(Skill(new string('x', 5000)));

            texto.Should().Contain("manual cortado");
            texto.Length.Should().BeLessThan(ConversationService.TetoDoManualDeSkill + 400);
        }
    }
}
