using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Sirius;

// Uma lista de linhas da tela que se troca de uma vez. Apagar e acrescentar linha por linha mandava um
// aviso por linha à lista: com 550 mudanças no Git, a janela parava quase um segundo a cada atualização,
// e marcar uma caixinha refazia tudo.
public sealed class RowList<T> : ObservableCollection<T>
{
    // Até quantas linhas diferentes valem a troca uma a uma, que mantém a rolagem e as linhas desenhadas.
    private const int FewChanges = 16;

    public void ReplaceAll(IEnumerable<T> rows)
    {
        var list = rows as IList<T> ?? rows.ToList();
        if (list.Count == Count)
        {
            var changed = new List<int>();
            for (var i = 0; i < Count && changed.Count <= FewChanges; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(this[i], list[i]))
                    changed.Add(i);
            }
            // Nada mudou: a lista não pisca nem fecha um menu aberto.
            if (changed.Count == 0)
                return;
            if (changed.Count <= FewChanges)
            {
                foreach (var i in changed)
                    this[i] = list[i];
                return;
            }
        }

        Items.Clear();
        foreach (var row in list)
            Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
