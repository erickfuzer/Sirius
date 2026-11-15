# Sirius: guia para quem trabalha no código

IDE nativa para Windows em C#, Avalonia 12 e AvaloniaEdit, sem WebView nem Electron. Este arquivo vale para qualquer assistente de código ou pessoa que vá mexer no projeto: como compilar, como o código está organizado, como testar e o que já custou caro descobrir.

## Idioma e convenções

- Interface, comentários, nomes de teste, mensagens de commit e notas de versão em português do Brasil. Comentários curtos, explicando o porquê, não o quê.
- Sem emoji na interface. Ícones são `Path` desenhados no XAML.
- Cores sempre pela `Palette` (`Theming/Palette.cs`). Todo recurso novo funciona nos dois temas, `claro` e `grafite`.
- Preferências ficam em `%APPDATA%\Sirius\settings.json` (`Core/AppSettings.cs`), inclusive o comando personalizado do Executar, que nunca é lido do projeto aberto. A detecção do comando só roda quando a pessoa clica em Executar.
- Interface, README e notas de versão não citam outros programas de PHP pelo nome. A busca por instalações conhecidas fica em `ToolPaths`, em silêncio.
- Todo recurso de teclado também precisa de botão, menu ou entrada na barra de comando (`Ctrl` `K`). Nada pode depender só de uma tecla, nem só de tecla F.
- Quem usa a Sirius costuma vir do VS Code. Antes de dar um recurso por pronto, confira como o VS Code faz o mesmo gesto.

## Comandos

```bash
dotnet run --project src/Sirius
dotnet test Sirius.slnx --blame-hang-timeout 120s
```

Instalador (Inno Setup 6), gerado em `artifacts\installer`, fora do Git:

```powershell
.\installer\build.ps1            # roda os testes antes
.\installer\build.ps1 -SkipTests
```

- No PowerShell 5.1, `git push` pode terminar com código 255 mesmo dando certo: confirme comparando `git ls-remote` com `git rev-parse HEAD`.
- No mesmo PowerShell, aspas duplas em `git commit -m` quebram o comando: use `git commit -F <arquivo>`.
- A identidade do Git do repositório usa o endereço `noreply` do GitHub. Nunca exponha um e-mail pessoal em commits.

## Estrutura

| Caminho | Conteúdo |
|---|---|
| `src/Sirius/MainWindow.axaml(.cs)` | Janela, árvore, abas, barra de comando, busca no projeto, diálogos e teclado |
| `src/Sirius/MainWindow.Panes.cs` | Dois lados do editor (`EditorPane` em `Editing/`): dividir, fechar, foco e cabeçalhos |
| `src/Sirius/MainWindow.Terminal.cs` | Cápsula, terminal e botão Executar |
| `src/Sirius/MainWindow.Language.cs` | Problemas, autocompletar, definição, dica do mouse, renomear símbolo e usos |
| `src/Sirius/MainWindow.Symbols.cs` | Lista de funções, classes e seletores do arquivo (`Ctrl` `Shift` `O`, `@` na barra) |
| `src/Sirius/MainWindow.Find.cs` | Procurar e substituir no arquivo (`Ctrl` `F`, `Ctrl` `H`, `F3`); lógica em `Core/FindInText` |
| `src/Sirius/MainWindow.Workspace.cs` | Vigilância do disco, sessão, aba sem nome, salvar sozinho, substituir no projeto |
| `src/Sirius/MainWindow.Git.cs`, `.GitHistory.cs`, `.GitChanges.cs` | Painel de Git, histórico, trecho alterado ("Como era antes") |
| `src/Sirius/MainWindow.Debug.cs`, `.DebugServer.cs` | Depuração de PHP com Xdebug, pelo Executar ou por páginas de um Apache |
| `src/Sirius/MainWindow.Browser.cs` | Botão Navegador: a página recarrega ao salvar (`Core/LiveServer`, `Core/ApacheSite`) |
| `src/Sirius/MainWindow.Mcp.cs` | O que o Claude Code vê pelo MCP e o botão Conectar |
| `src/Sirius/MainWindow.Settings.cs` | Configurações em seções; cada botão tem um `Tag` como `recuo:2`, tratado em `ChangeSettingAsync` |
| `src/Sirius/MainWindow.Updates.cs` | Aviso de versão nova e "Atualizar agora" |
| `src/Sirius/MainWindow.Editing.cs` | Comentar, duplicar e mover linhas, ir para linha, quebra de linha, formatar, menu do botão direito |
| `src/Sirius/MainWindow.Snippets.cs`, `.Emmet.cs`, `.Pairs.cs`, `.Colors.cs`, `.Images.cs`, `.Svg.cs`, `.Drop.cs` | Atalhos com campos, Emmet, pares que se fecham, seletor de cores, aba de imagem, prévia do SVG, arrastar do Explorer |
| `src/Sirius/Core/` | Lógica sem interface: Git, Xdebug, Emmet, busca, MCP, servidor do navegador, configurações |
| `src/Sirius/Editing/` | Documento aberto, sintaxe (TextMate), margem com o fio, desenhos sobre o texto |
| `src/Sirius/Lsp/` | Conexão, cliente e catálogo dos servidores de linguagem |
| `src/Sirius/Terminal/` | ConPTY, sequências, tela e teclas |
| `src/Sirius/Theming/` | Paletas, cores do terminal e ícones de arquivo |
| `src/Sirius/Assets/` | Fontes (OFL), tabelas do Emmet e atalhos do VS Code (MIT), ícones do Material Icon Theme (MIT) |
| `tests/Sirius.Tests/` | xUnit v3 e Avalonia.Headless, com nomes de teste em português |
| `installer/` | `sirius.iss`, `build.ps1` e `notas/` de cada versão |
| `docs/` | Site no GitHub Pages, prints do README e social preview |

## Arquitetura

- **Sem MVVM.** A janela é code-behind em arquivos `partial`, e as listas usam registros de `Rows.cs`, recriados quando algo muda.
- **Dois lados do editor.** Cada lado é um `EditorPane`, com editor, margem (`GutterMargin`), cores (`TypographicColorizer`), quadradinhos de cor do CSS e o documento que mostra. O lado com o foco é "o" editor: `CurrentEditor`, `Gutter`, `Colorizer` e `Swatches` apontam para ele, e tudo o que age no código passa por eles. As abas são uma fileira só, e a aba clicada abre no lado com o foco.
- **Linguagem.** Servidores LSP de verdade (Intelephense, `vscode-langservers-extracted`, `csharp-ls` e outros), instalados pelo `npm` ou `dotnet tool` quando faltam. O cliente fica em `Lsp/LanguageClient.cs`, e as respostas são lidas em `Lsp/LspModels.cs`.
- **Terminal.** ConPTY do Windows, desenhado pela própria Sirius (`Terminal/`).
- **Git.** O `git` instalado na máquina, sem biblioteca no meio (`Core/GitClient.cs`).
- **Depuração.** Xdebug baixado para `%LOCALAPPDATA%\Sirius\xdebug`, conferido por SHA256 com a lista fixa em `XdebugCatalog`, e ligado por `PHP_INI_SCAN_DIR` só durante o Executar. Para páginas de um Apache, um bloco marcado vai no `php.ini`, com cópia do original.
- **Navegador.** Um servidor da Sirius em `localhost:5500` (`HttpListener`, só o próprio computador) repassa as páginas ao servidor do Executar ou ao Apache, ou serve a pasta, e acrescenta um script que escuta a recarga por `EventSource`.
- **Claude Code (MCP).** `Sirius.exe --mcp` é uma ponte MCP por stdio (`Core/McpBridge.cs`, JSON-RPC à mão) que conversa com a janela da pasta aberta por um pipe só do usuário (`Core/McpChannel.cs`). Só leitura. A Sirius não usa a conta do Claude nem chama o Claude sozinha.
- **Atualização.** "Atualizar agora" baixa o `Setup.exe` da última Release do GitHub, confere tamanho e SHA256 com o `digest` do GitHub, instala em silêncio e reabre.

## Direção visual

Simples e direta, com design marcante, sem copiar marcas.

- Sintaxe colorida, não só peso de fonte.
- Interface compacta, pensada para monitores 2K.
- O destaque é um azul-acinzentado apagado, e os fundos escuros são grafite, só um pouco frios. Não aumente a saturação, e não use laranja na interface (laranja em conteúdo, como o ícone do HTML ou a sintaxe, tudo bem).
- A cápsula do terminal tem tom baixo e leve transparência no escuro. Mais transparência embola o código de trás com o texto, e o Avalonia não desfoca só um pedaço da janela.

Assinaturas a preservar: o ícone Estrela dupla (`Assets/sirius.ico`), o fio vertical até a linha ativa com uma estrelinha na ponta, os comentários em IBM Plex Mono Italic e a cápsula invertida flutuante que se abre em terminal.

## Como verificar

Seja honesto sobre o que foi verificado, separando teste automático, imagem gerada sem janela e janela real.

1. **Testes.** Confira o total ao final: se for menor que o esperado, o processo travou ou caiu no meio. Em mudanças com threads, terminal, disco ou rede, rode a suíte várias vezes, porque já houve falhas intermitentes.
2. **Linguagem.** Teste com o servidor real, inclusive PHP. O servidor falso dos testes (`FakeLanguageServer`) não revelou problemas de caminho, `PATH`, hífen e cursor.
3. **Imagem sem janela.** Um programa temporário fora do repositório, referenciando `src/Sirius/Sirius.csproj`, `Avalonia.Headless` e `Avalonia.Skia`:

   ```csharp
   AppBuilder.Configure<App>().UseSkia()
       .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
       .SetupWithoutStarting();
   // Crie a MainWindow, chame Show() e processe com Dispatcher.UIThread.RunJobs()
   // e AvaloniaHeadlessPlatform.ForceRenderTimerTick(). Depois:
   window.CaptureRenderedFrame()!.Save("tela.png");
   ```

   Sem processar a fila em um laço, um `await` trava. Sugestões e dicas são janelas à parte e não aparecem.
4. **Janela real.** Um programa temporário com `UsePlatformDetect` abre a `MainWindow` de verdade. Guarde e restaure o `settings.json` de quem usa a máquina. Teclas e mouse vão por `PostMessage` (`WM_KEYDOWN`, `WM_CHAR`, `WM_LBUTTONDOWN`), sem usar o teclado e o mouse da pessoa, e a captura por `PrintWindow`. Limites:
   - Duplo clique são dois pares de down e up, porque o Avalonia ignora `WM_LBUTTONDBLCLK`.
   - `Ctrl` e `Shift` não chegam de fora. Na thread da interface, use `SetKeyboardState` em `0x10`/`0x11` e também `0xA0`/`0xA2` (o Avalonia olha o lado esquerdo ou direito), poste a tecla e devolva o estado. A tecla com `Ctrl` pode virar letra no editor, porque o `TranslateMessage` não vê o estado: é do teste, não da Sirius.
   - `Alt`: `0x12` e `0xA4` no estado, e `WM_SYSKEYDOWN` com o bit 29 no `lParam`.
   - O Avalonia ignora `WM_CHAR` até a próxima tecla depois de um `Enter` ou de um clique: mande uma tecla neutra (`0x91`) antes de digitar.
   - Parar o mouse é cancelado quando o cursor real está fora. Chame por reflexão `PointerHoverLogic.OnPointerHover` com `_hoverLastEventArgs` do `TextView`.
   - As fotos de `GetWindowRect` têm 8 pixels a mais à esquerda que as coordenadas da janela.
   - `PrintWindow` não guarda transparência: cantos arredondados de janelas à parte saem escuros. Para ver o que aparece na tela, copie com `BitBlt` de `GetDC(IntPtr.Zero)`.
   - Faça o teste distinguir: ao simular um atalho, escolha um caso em que a tecla sem o modificador daria outro resultado.
5. **Git de ponta a ponta.** Com `GIT_CONFIG_GLOBAL` temporário só no programa de teste e um `git init --bare` local como remoto, sem login.
6. **Depuração.** Use um comando como `php -d display_errors=1 -S localhost:8132`, que a Sirius não reconhece como site e por isso não abre o navegador. Baixe o Xdebug para a pasta de trabalho do teste com `XdebugSetup.DownloadAsync` e troque `PrepareXdebug`; a "página" pode ser um `HttpClient`.
7. **Apache.** Nunca o da pessoa: `httpd.exe -f <conf temporária>` com o `ServerRoot` do Apache instalado, outra porta, `PHPINIDir` numa cópia do `php.ini` e `Process.Kill(true)` no fim. Leia o `error.log` com `FileShare.ReadWrite`.
8. **Navegador.** Uma Sirius de teste aberta, com `OpenUrl` trocado para não abrir o navegador da pessoa, e um navegador controlado pelo agente lendo `localhost:5500` (estilo calculado, `performance.getEntriesByType`).
9. **Instalador.** Gere uma cópia com outro `AppId`, outro nome e outra pasta, e sem as chaves de registro, para testar instalar e atualizar sem tocar na instalação de verdade. Passe `/LOG=` para ler o que aconteceu.
10. **Soltar arquivos.** Pegue o `IDropTarget` com `GetProp(hwnd, "OleDropTargetInterface")` e chame `DragEnter` e `Drop` pela tabela do COM. O dado vem de `SHCreateShellItemArrayFromIDLists` com `BHID_DataObject`; `SHCreateDataObject` é recusado em silêncio.

## Cuidados com a máquina de quem testa

- **Nunca apague `%LOCALAPPDATA%\Sirius\xdebug`.** Um `php.ini` ligado por "Depurar páginas do servidor" aponta para a dll de lá, e o PHP passa a dar "Failed loading".
- Nunca altere o `php.ini` nem o Apache de quem usa a máquina num teste, nem encerre processos pelo nome (`taskkill /IM httpd.exe`).
- Nunca rode o instalador de verdade numa máquina com a Sirius instalada: ele divide `AppId`, atalhos e registro com a cópia da pessoa.
- Nunca ponha o `.exe` em um commit: ele vai só na Release.

## Armadilhas

**Editor e interface**
- **Dois lados.** Nunca use o `CodeEditor` gerado pelo XAML no código: ele é sempre o lado esquerdo. Use `CurrentEditor`. O que se pendura no editor ao abrir a janela vai num `foreach (var pane in _panes)`, e o evento que pode vir do outro lado (cursor, rolagem, seleção) confere `pane == _pane`. O que depende do arquivo (erros, linha parada, pontos de parada) usa `pane.Document`, nunca `ActiveDocument`, senão um lado desenha o arquivo do outro. As `TextEditorOptions` são a mesma instância nos dois lados. Nos testes, `FindControl("CodeEditor")` é o lado esquerdo.
- **AltGr** chega como `Ctrl` + `Alt` e digita `?` e `/` no ABNT2: trate como digitação, nunca como atalho.
- **`Ctrl` `/`:** no ABNT2, a tecla `VK_OEM_2` é o `;`, e o `/` tem tecla própria (`AbntC1`). O comentário aceita `Oem2`, `AbntC1` e o `/` do teclado numérico.
- **Janela em segundo plano:** o Windows recusa o foco, e `Activate` não sobe a janela. `BringToFrontForPause` liga e desliga `Topmost`.
- **Posição sob o mouse:** `GetPosition` arredonda para a emenda mais próxima. Use `GetPositionFloor`.
- **Threads de fundo:** nunca chame `Dispatcher.UIThread` nelas. Guarde o `Dispatcher` no construtor e use `Post`.
- **Redesenhar de dentro do desenho:** um método chamado enquanto a barra de comando monta a lista não pode redesenhá-la de novo, senão vira estouro de pilha (`0xc00000fd`). Peça de novo por temporizador.
- **Ícones em `Path`:** espaço vazio à esquerda ou em cima tira o ícone do centro. `IconCenterTests` mede.
- **Listas grandes:** um `ItemsPresenter` em modelo próprio precisa de `ItemsPanel="{TemplateBinding ItemsPanel}"`, senão o Avalonia desenha tudo. Troque as linhas com `RowList.ReplaceAll`, e role com `ScrollIntoView`, porque linha fora da tela não existe.
- **Grid com linha escondida:** um controle com `Grid.RowSpan` sobre uma linha `Auto` vazia tem a altura dividida entre as duas. Para achar sobras assim, imprima os `Bounds` em vez de adivinhar.
- **Elementos sem texto no editor:** um `InlineObjectElement` de comprimento zero ocupa coluna sem ocupar caractere, por isso `OnKeyDownAtSwatch`.
- **Escapes em C#:** `"\x1bb"` vira outro caractere. Antes de letra ou dígito hexadecimal, escreva `\u001b`.
- **Busca preenchida por código:** o `TextChanged` chega depois de mudar o `Text`. A busca no arquivo parte de `_findAnchor`, e os usos guardam o nome em `_usesQuery`.
- **Diálogo aberto por um clique:** abra ao soltar o botão, não ao apertar, senão o que vem depois do clique rouba o foco da caixa.
- **Controle novo no painel da cápsula:** deixe `IsVisible="False"`, senão fica por cima do terminal.

**Emmet, sugestões e pares**
- Copie o comportamento do código do Emmet (`emmetio/emmet` v2.4.11), não da memória: campos numerados por valor, atributo repetido no lugar do primeiro, filho em linha sem quebra.
- Use o `textEdit` do servidor nas sugestões, senão `background-co` vira `background-background-color`. Filtre antes de cortar a lista.
- Com itens do servidor, o AvaloniaEdit filtra pelo que vem depois do começo da palavra; só a lista sem servidor (`_localOnlyCompletion`) é refeita a cada tecla.
- No `.sass` não há servidor: a lista só tem o Emmet, e uma tag sozinha não vira Emmet ali.
- Só o fechamento que a Sirius acrescentou pula ou some no `Backspace` (`_pairClosers`). O par entra em `TextEntering`, então `SuggestAfterTyping` é chamado à mão.

**Terminal**
- Inscreva-se nos eventos do ConPTY antes de `Begin()`, senão a saída de programas rápidos se perde.
- Fixe o tamanho final antes de iniciar a sessão.
- Símbolo de outra fonte empurra o texto: `DrawRun` desenha cada um na sua célula, e `BlockElements` desenha blocos com retângulos.
- Enviar e Receber passam pelo `cmd`: nunca ponha nome de branch ou remoto sem validar (`SafeRemoteName`, `SafeBranchName`).

**Linguagem**
- O Intelephense envia `file:///c%3A/...`: use sempre `LspParsers.ToPath`.
- `ToolPaths.EnsureOnPath` acrescenta as pastas conhecidas ao `PATH`, senão ferramentas instaladas com a Sirius aberta morrem ao iniciar.
- Responda `workspace/configuration` com objeto vazio, nunca `null`, senão o servidor de CSS quebra em silêncio.
- Os servidores de HTML e CSS só formatam com `provideFormatter` nas `InitializationOptions`.
- `GrammarCatalog` troca `source.php` por `text.html.php`, senão o `#` do CSS vira comentário.
- Intelephense, HTML e CSS respondem o `documentSymbol` em árvore; o servidor pode ainda estar abrindo quando a lista é pedida.

**Arquivos e imagens**
- Salvar passa por arquivo temporário e mantém a codificação; criar nunca sobrescreve; excluir vai para a Lixeira. Não enfraqueça isso.
- O texto de uma aba de imagem é vazio: nada pode gravá-lo, e quem lê `Document.Text` precisa olhar `IsImage`.
- SVG: Svg.Skia 5.1.1 (a última para o Skia do Avalonia 12). `SvgRender` desliga tudo que busca fora do texto, e o desenho roda numa thread de pilha grande, porque estouro de pilha não se trata com `try`.
- `Bitmap` ignora o EXIF: `ImageFile` lê pelo `SKCodec`.
- Soltar arquivos: responda sempre `Copy`, porque `Move` apaga o original.
- Ícones de arquivo: o `DrawingImage` mede pelo que foi pintado. Para trocar, baixe os SVG da tag do Material Icon Theme e rode `Assets/FileIcons/gerar.py`.
- Mover pelo Explorer chega como apagar e criar, sem `Renamed`: `DiskMoves` acha pelo nome e pela data.

**Git**
- Sem Git instalado, `Process.Start("git")` falha igual a uma pasta sem repositório: `IsGitInstalled` separa os casos.
- Leituras em pastas desconhecidas não podem executar nada da configuração do repositório: `GitClient` desliga `core.fsmonitor`, usa `--no-optional-locks` e `--no-show-signature`.
- O primeiro Receber, sem ligação com o remoto, só traz o branch de mesmo nome, com `--allow-unrelated-histories`.
- Um arquivo editado e não salvo não aparece no status: salve e atualize antes de montar o commit.
- O conteúdo apagado de um renomeado vem de `cat-file --batch`, nunca de `hash-object`, que roda os filtros do repositório.

**PHP e depuração**
- Com `output_buffering` no `php.ini`, nada sai do PHP até o fim. Cada parada lê `ob_get_contents()` por `eval`. Teste com e sem buffer.
- O Xdebug só para em linha com código PHP; HTML puro entre `?>` e `<?php` nunca para. A margem segue a linha que o Xdebug escolhe (`BreakpointMoved`).
- O Xdebug para uma vez em cada comando PHP da linha marcada (`$a = 1; $b = 2;` para duas vezes). Não é da Sirius.
- Um PHP que já tem Xdebug não carrega outro.
- O `php.ini` pode estar em ANSI: `PhpIniXdebug` lê e grava bytes, e o bloco é só ASCII.
- O PHP instalado pelo `winget` trava pedindo administrador pela dependência do Visual C++: `--skip-dependencies` quando ela já existe.

**Navegador**
- Só `.html`, `.htm`, `.php`, `.css`, `.js` e `.mjs` recarregam; dados e imagens não, senão uma página que grava um arquivo a cada visita recarregaria sem parar.
- Depois de um POST, a recarga volta pela mesma URL (`data-post`), em vez de reenviar o formulário.
- O repasse manda o `Host` da Sirius ao site, senão o PHP montaria links que saem da recarga, e responde com `Cache-Control: no-cache`, senão um F5 comum mostra o CSS velho.
- A recarga só vale para a página aberta pelo botão Navegador, em `localhost:5500`. Um endereço digitado à mão vai direto ao servidor.

**Instalador e atualização**
- Nunca limpe `{app}` com curinga. `sirius.iss` precisa de BOM UTF-8, `build.ps1` fica só em ASCII, e o `AppId` nunca muda.
- Cada sessão do Claude Code conectada mantém um `Sirius.exe --mcp` sem janela. O instalador encerra esses processos no `PrepareToInstall` antes de copiar, porque o Restart Manager não os fecha (e às vezes atribui as dlls deles ao "System") e o instalador silencioso desistiria sem aviso. `CloseApplications=force` fica.
- Se a atualização falhar, o `DeinitializeSetup` reabre a Sirius antiga, e o `SetupLogging` deixa um `Setup Log` na pasta temporária.
- O botão de baixar do site procura na última Release um arquivo terminado em `Setup.exe`: não mude esse final do nome.

**Testes**
- Total menor que o esperado: rode de novo com `--blame-crash`, leia o `Sequence_*.xml` mais novo em `tests\Sirius.Tests\TestResults` (os `completed=False` estavam rodando) e o Visualizador de Eventos ("Application Error"). `0xc00000fd` é estouro de pilha.
- Com servidores de linguagem instalados, abrir `.php` ou `.cs` num teste os inicia de verdade. Use `window.Language.IsInstalled = _ => false` ou `FakeLanguageServer`.
- Git nos testes: `GitEnvironment` com `GIT_CONFIG_GLOBAL` temporário. Não altere variáveis de ambiente do processo, porque os testes rodam em paralelo.
- Um `await` em confirmação que ninguém responde trava a suíte; fechar aba com alteração também pergunta.
- Sem tela, a animação da cápsula não anda e o clique cai fora: use `RaiseEvent` e confira na janela real.

**Ambiente de agentes no Windows**
- Script `.ps1` com acento precisa de BOM, senão o PowerShell 5.1 lê como ANSI.
- `sed` e `perl` comem barras invertidas de caminhos do Windows, e um `\n` dentro de heredoc vira quebra de linha de verdade. Para editar código com barras invertidas, use uma ferramenta de edição de arquivo, não o shell.
- `Get-Content` sem `-Encoding UTF8` lê como ANSI, e regravar estraga os acentos.
- Num app empacotado (como um assistente instalado pela loja), gravar em `%LOCALAPPDATA%` e `%APPDATA%` pode cair numa cópia virtual que só os programas abertos dali enxergam, enquanto apagar apaga o de verdade. Para ler o que o usuário vê, use o caminho real por `\\localhost\C$\Users\<nome>\AppData\...`. Um processo criado por `Invoke-CimMethod Win32_Process -MethodName Create` roda fora do pacote.
- Variáveis do assistente, como `NO_COLOR`, `CLAUDE_*` e `ANTHROPIC_*`, são herdadas por tudo que ele abre: tire-as antes de testar como o usuário usa.

## Versões e publicação

Recurso novo sobe o segundo número (`1.9.0`), só correção sobe o terceiro (`1.8.1`). Versões pequenas, a cada um ou dois recursos.

1. Aumente só a linha `Version` em `src/Sirius/Sirius.csproj` (regravar o arquivo inteiro pelo PowerShell tira o BOM), escreva `installer/notas/<versão>.md` no formato das anteriores, rode os testes e faça o commit.
2. Gere com `.\installer\build.ps1 -SkipTests` e teste o programa empacotado em janela real.
3. Envie a `main`, crie a tag e envie com `git push origin refs/tags/<tag>`.
4. `gh release create <tag> <setup.exe> --latest --title "Sirius <versão>" --notes-file installer/notas/<versão>.md`.
5. Baixe o instalador da Release, compare o SHA256 com o local e com o `digest` do GitHub, e confira que `releases/latest` aponta para a versão nova.

Sempre Release normal, nunca pré-lançamento, que fica de fora de `releases/latest`. O instalador não é assinado, então o Windows mostra um aviso.

As notas e o README trazem um convite curto para relatar problemas em Issues. Ao acrescentar um recurso ou um limite, atualize as duas listas do README (funcionalidades e limitações) e confira se o site em `docs/` continua dizendo a verdade. Os prints de `docs/` vêm de janela real, com projeto neutro, sem caminhos da máquina nem nome de ninguém.
