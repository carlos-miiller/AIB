using System;
using System.Collections.Generic;
namespace AIB.Services {
    public class Reminder {
        public bool IsApproaching { get; set; }
        public bool IsActive { get; set; }
        public DateTime TriggerTime { get; set; }
    }
    /// <summary>
    /// ESBOÇO INERTE — nada aqui faz nada.
    /// <para>
    /// <c>ActiveReminders</c> devolve uma lista NOVA e vazia a cada chamada, então quem se
    /// vincula a ela se vincula a um descartável; <c>DeleteReminder</c> e <c>CancelReminder</c>
    /// têm corpo vazio. É assinatura de serviço sem serviço atrás.
    /// </para>
    /// <para>
    /// Existe por UM chamador: a <see cref="AIB.Views.ContextSidebar"/>, que também está
    /// estacionada. Lembretes não constam da spec de chat e ficaram fora da tela viva.
    /// </para>
    /// <para>
    /// Quem for implementar lembretes começa APAGANDO este arquivo. Reaproveitar os corpos
    /// vazios daria um recurso que roda sem erro e sem efeito — a pior das duas falhas.
    /// </para>
    /// </summary>
    public static class ReminderService {
        public static List<Reminder> ActiveReminders => new List<Reminder>();
        public static void DeleteReminder(int id) { }
        public static void CancelReminder(Reminder r) { }
    }
}
