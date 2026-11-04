<div align="center">

# 🌟 Sirius

IDE **nativa** para Windows, simples e direta, sem WebView nem Electron. Editor, terminal, Git e inteligência de código no mesmo lugar, numa interface que sai da frente do seu código.

![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge)
![.NET 10](https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Avalonia](https://img.shields.io/badge/Avalonia-8B44AC?style=for-the-badge&logo=avaloniaui&logoColor=white)
![Windows](https://img.shields.io/badge/Windows_10_e_11-0078D4?style=for-the-badge)

<img src="docs/screenshot-editor.png" alt="Sirius no tema grafite com um arquivo PHP aberto, a árvore de arquivos com marcas do Git e a cápsula de comandos na parte de baixo" width="760">
<br><br>

[![Baixar o instalador](https://img.shields.io/badge/▶_Baixar_o_instalador-3F5F8F?style=for-the-badge)](https://github.com/erickfuzer/Sirius/releases/latest)

</div>

> [!NOTE]
> A Sirius está em **Alpha**: funciona no dia a dia, mas espere arestas. Faça commit do seu trabalho e relate o que encontrar em [Issues](https://github.com/erickfuzer/Sirius/issues).

## Funcionalidades

- **Editor:** abas, sintaxe colorida para dezenas de linguagens, zoom, temas claro e grafite e modo foco
- **Cores do CSS:** um quadradinho mostra cada cor no código, e clicar nele abre um seletor que altera o texto na hora. Funciona em CSS, SCSS, Sass e Less, e dentro de `<style>` e `style="..."` em HTML e PHP
- **Emmet:** em HTML e PHP, `div.cartao>h2+p` + <kbd>Tab</kbd> vira o HTML pronto, e `!` cria a página inicial. O <kbd>Tab</kbd> seguinte pula para o próximo campo vazio, e isso funciona sem instalar nada
- **Barra de comando:** <kbd>Ctrl</kbd> + <kbd>K</kbd> encontra arquivos e executa ações sem tirar a mão do teclado
- **Projeto:** árvore de arquivos para criar, renomear e excluir, com busca e substituição em todo o projeto
- **Terminal integrado:** a cápsula flutuante se abre em um terminal completo. O botão Executar detecta o comando do projeto, e os erros da saída são clicáveis. Em PHP, ele sobe o servidor e oferece abrir o site no navegador, ou o arquivo em uso em pastas sem `index.php`. Se o computador não tiver PHP, oferece a instalação
- **Inteligência de código:** autocompletar, erros no código, ir para a definição, dicas e renomear símbolo. Se faltar o suporte a uma linguagem, a própria IDE oferece a instalação
- **Git:** mudanças na árvore e na margem, comparação lado a lado, commit por arquivo, descartar, enviar, receber e branches
- **Sessão:** cada pasta reabre as suas abas, e a IDE acompanha o que outros programas mudam no disco
- **Aviso de versão nova:** um aviso discreto na cápsula leva à página da versão, sem baixar nada sozinho. Dá para ignorar uma versão ou desligar o aviso pela barra de comando
- **Cuidado com o seu trabalho:** salvar mantém a codificação original do arquivo e passa por um arquivo temporário, criar nunca sobrescreve e excluir vai para a Lixeira
- **Pronta para ABNT2:** o <kbd>AltGr</kbd> nunca aciona um atalho, então `?` e `/` funcionam normalmente

<details>
<summary>Ver as cores do CSS</summary>
<br>
<img src="docs/screenshot-cores.png" alt="Arquivo CSS com um quadradinho na frente de cada cor e o seletor aberto, com o quadro de cor e as barras de tom e transparência" width="760">
</details>

<details>
<summary>Ver o painel de Git</summary>
<br>
<img src="docs/screenshot-git.png" alt="Painel de Git com a lista de mudanças e a comparação lado a lado de um arquivo PHP" width="760">
</details>

<details>
<summary>Ver o terminal</summary>
<br>
<img src="docs/screenshot-terminal.png" alt="Terminal aberto a partir da cápsula, mostrando o histórico e o estado do Git" width="760">
</details>

<details>
<summary>Ver a barra de comando</summary>
<br>
<img src="docs/screenshot-comando.png" alt="Barra de comando aberta sobre o editor, listando arquivos e ações com seus atalhos" width="760">
</details>

<details>
<summary>Ver o tema claro</summary>
<br>
<img src="docs/screenshot-claro.png" alt="Sirius no tema claro com o mesmo arquivo PHP aberto" width="760">
</details>

## Como funciona

A interface é desenhada pelo [Avalonia](https://avaloniaui.net), sem navegador embutido, e o editor usa o [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) com gramáticas TextMate para a sintaxe.

O terminal roda no ConPTY, o mesmo mecanismo de terminal do próprio Windows. A inteligência de código conversa com servidores de linguagem pelo protocolo LSP: quando falta o servidor de um arquivo aberto, a cápsula oferece a instalação para C#, PHP, HTML, CSS, JavaScript, TypeScript e Python. Go, Rust e C++ funcionam se `gopls`, `rust-analyzer` ou `clangd` estiverem instalados. O Git é o seu próprio `git`, sem biblioteca no meio.

A única consulta que a Sirius faz por conta própria é perguntar à API pública do GitHub qual é a última versão, no máximo uma vez por dia. Ela não envia nada do seu projeto e pode ser desligada em "Aviso de versão nova", na barra de comando.

## Como usar

**Instalador:** baixe o `Setup.exe` em [Releases](https://github.com/erickfuzer/Sirius/releases/latest) e abra. Ele instala só para o seu usuário, sem pedir administrador, e já leva o .NET junto. Requer Windows 10 versão 1809 ou mais recente, em 64 bits.

> O instalador ainda não tem assinatura digital, então o Windows pode mostrar um aviso azul. Clique em **Mais informações** e depois em **Executar assim mesmo**.

**Pelo código:** com o [SDK do .NET 10](https://dotnet.microsoft.com/download):

```bash
git clone https://github.com/erickfuzer/Sirius.git
cd Sirius
dotnet run --project src/Sirius
```

Para gerar o instalador, instale também o [Inno Setup 6](https://jrsoftware.org/isinfo.php) e rode o script abaixo. O arquivo sai em `artifacts\installer`.

```powershell
.\installer\build.ps1
```

### Atalhos

| Atalho | Ação |
|---|---|
| <kbd>Ctrl</kbd> <kbd>K</kbd> | Barra de comando: encontrar arquivos e executar ações |
| <kbd>Ctrl</kbd> <kbd>O</kbd> | Abrir pasta |
| <kbd>Ctrl</kbd> <kbd>N</kbd> | Nova aba sem nome |
| <kbd>Ctrl</kbd> <kbd>S</kbd> | Salvar |
| <kbd>Ctrl</kbd> <kbd>Shift</kbd> <kbd>S</kbd> | Salvar como |
| <kbd>Ctrl</kbd> <kbd>W</kbd> | Fechar aba |
| <kbd>Ctrl</kbd> <kbd>F</kbd> | Procurar no arquivo |
| <kbd>Ctrl</kbd> <kbd>Shift</kbd> <kbd>F</kbd> | Buscar em todo o projeto (<kbd>Enter</kbd> avança pelos resultados) |
| <kbd>Ctrl</kbd> <kbd>.</kbd> | Modo foco |
| <kbd>F5</kbd> | Executar ou parar o projeto |
| <kbd>Ctrl</kbd> <kbd>J</kbd> | Abrir ou fechar o terminal |
| <kbd>Ctrl</kbd> <kbd>Shift</kbd> <kbd>G</kbd> | Painel de Git: mudanças, comparação, commit, enviar e receber |
| <kbd>Ctrl</kbd> <kbd>+</kbd> e <kbd>Ctrl</kbd> <kbd>-</kbd> | Aumentar ou diminuir a fonte do editor (<kbd>Ctrl</kbd> <kbd>0</kbd> volta ao padrão) |
| <kbd>F2</kbd> | Renomear o item da lista de arquivos ou, no editor, o símbolo sob o cursor |
| <kbd>F12</kbd> ou <kbd>Ctrl</kbd> + clique | Ir para a definição |
| <kbd>Ctrl</kbd> <kbd>Espaço</kbd> | Mostrar sugestões |
| <kbd>Tab</kbd> | Em HTML e PHP, expandir uma abreviação do Emmet e pular entre os campos vazios |

### Para desenvolver

- **Testes:** `dotnet test Sirius.slnx`. Usam xUnit e rodam a janela de verdade, sem abrir nada na tela.
- **Preferências:** ficam em `%APPDATA%\Sirius\settings.json`.

## Limitações

- Só funciona no Windows: o terminal, o instalador e a integração com o Explorer são específicos dele.
- Ainda não tem depuração nem histórico de commits dentro da IDE.
- Conflitos de Git são resolvidos editando o arquivo.
- Programas de tela cheia no terminal, como `vim`, não foram testados.
- As atualizações ainda são manuais: a IDE avisa, mas você baixa e instala a versão nova por cima.

## Estrutura

```
Sirius/
├── src/Sirius/
│   ├── MainWindow.*        # janela, abas, árvore, busca, terminal, linguagem e Git
│   ├── Core/               # lógica sem interface: arquivos, busca, Git e preferências
│   ├── Editing/            # documento aberto, sintaxe e margem com o fio
│   ├── Lsp/                # conexão com os servidores de linguagem
│   ├── Terminal/           # ConPTY, interpretador de sequências e tela do terminal
│   ├── Theming/            # temas claro e grafite
│   └── Assets/             # ícone e fontes
├── tests/Sirius.Tests/     # testes da lógica e da janela
├── installer/              # script do instalador (Inno Setup), build.ps1 e notas das versões
├── docs/                   # prints do README e da prévia de link
├── LICENSE
└── README.md
```

## Créditos

Interface feita com [Avalonia](https://avaloniaui.net) e [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit), com sintaxe pelo [TextMateSharp](https://github.com/danipen/TextMateSharp), todos sob a licença MIT. As fontes [Geist](https://vercel.com/font) e [IBM Plex Mono](https://www.ibm.com/plex/) são distribuídas sob a licença SIL Open Font, com os textos em `src/Sirius/Assets/Fonts`. Desenvolvido com o auxílio do [Claude](https://claude.ai) (Anthropic).

## Licença

[MIT](LICENSE): use, modifique e distribua à vontade.

<div align="center">
<sub>Feito por <a href="https://github.com/erickfuzer">Erick Fuzer</a></sub>
</div>
