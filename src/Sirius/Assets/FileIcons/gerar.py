# Gera icons.json com os ícones de arquivo do Material Icon Theme (licença MIT, ao lado).
# Uso: python gerar.py <pasta com os .svg baixados de icons/ na versão abaixo, todos os folder-*.svg e o
# src/core/icons/folderIcons.ts da mesma versão>
# Cada ícone vira a caixa do viewBox e as camadas (cor e caminho), com os comandos do caminho
# separados por espaço, para o leitor do Avalonia não tropeçar em "a1 1 0 01.5.5".
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

VERSION = "v5.40.0"
ICONS = [
    "html", "php", "css", "sass", "less", "javascript", "typescript", "react", "react_ts", "vue", "svelte",
    "json", "nodejs", "markdown", "readme", "license", "document", "image", "svg", "xml", "yaml", "toml",
    "toml_light", "settings", "tune", "git", "console", "powershell", "database", "csharp", "visualstudio",
    "xaml", "c", "h", "cpp", "hpp", "python", "java", "go", "rust", "ruby", "log", "lock", "zip", "pdf",
    "table", "word", "font", "audio", "video", "exe", "certificate", "key", "docker", "twig", "laravel",
    "phpunit", "todo",
]

ARGS = {"M": 2, "L": 2, "T": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "A": 7, "Z": 0}
NUMBER = re.compile(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")


def number(text):
    if text.startswith("."):
        return "0" + text
    if text.startswith("-."):
        return "-0" + text[1:]
    if text.startswith("+"):
        return number(text[1:])
    return text


def normalize(d):
    out = []
    i = 0
    command = None
    args = []

    def flush():
        nonlocal command, args
        size = ARGS[command.upper()]
        if size == 0:
            out.append(command)
            args = []
            return
        if len(args) % size != 0 or not args:
            raise ValueError(f"argumentos soltos em {command}: {args}")
        current = command
        for start in range(0, len(args), size):
            out.append(current + " " + " ".join(args[start:start + size]))
            # Depois do M, os pares seguintes são retas.
            if current in "Mm":
                current = "L" if current == "M" else "l"
        args = []

    while i < len(d):
        c = d[i]
        if c.isspace() or c == ",":
            i += 1
        elif c.upper() in ARGS:
            if command is not None:
                flush()
            command = c
            i += 1
        else:
            if command is None:
                raise ValueError("número antes do comando")
            # No arco, as duas bandeiras são um dígito só e podem vir grudadas.
            if command in "Aa" and len(args) % 7 in (3, 4):
                if c not in "01":
                    raise ValueError(f"bandeira inválida: {d[i:i + 10]}")
                args.append(c)
                i += 1
                continue
            match = NUMBER.match(d, i)
            if not match:
                raise ValueError(f"não li: {d[i:i + 20]}")
            args.append(number(match.group()))
            i = match.end()
    if command is not None:
        flush()
    return " ".join(out)


def convert(path):
    root = ET.parse(path).getroot()
    box = [float(v) for v in root.get("viewBox").split()]
    inherited = root.get("fill")
    layers = []
    for element in root:
        tag = element.tag.split("}")[1]
        if tag != "path":
            raise ValueError(f"{path}: elemento {tag}")
        for attribute in element.attrib:
            if attribute not in ("d", "fill"):
                raise ValueError(f"{path}: atributo {attribute}")
        fill = element.get("fill", inherited)
        if fill in (None, "none"):
            continue
        if not re.fullmatch(r"#[0-9a-fA-F]{6}", fill):
            raise ValueError(f"{path}: cor {fill}")
        layers.append([fill.lower(), normalize(element.get("d"))])
    return {"box": box, "layers": layers}


# Pastas. Todo SVG de pasta do Material tem o mesmo formato (checkFolderIconStructure.ts): a pasta fechada,
# com id="folder", e o desenho de dentro, com id="motive". A aberta não existe no repositório: o Material a
# gera trocando só o caminho da pasta, e a Sirius faz o mesmo. A pasta comum também é gerada, nesta cor.
FOLDER_COLOR = "#90a4ae"
FOLDER_CLOSED = "m6.922 3.768-.644-.536A1 1 0 0 0 5.638 3H2a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1H7.562a1 1 0 0 1-.64-.232"
FOLDER_OPEN = "M14.483 6H4.721a1 1 0 0 0-.949.684L2 12V5h12a1 1 0 0 0-1-1H7.562a1 1 0 0 1-.64-.232l-.644-.536A1 1 0 0 0 5.638 3H2a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h11l2.403-5.606A1 1 0 0 0 14.483 6"
XLINK = "{http://www.w3.org/1999/xlink}href"


def folder_names(folder):
    # Os nomes de cada ícone de pasta, em folderIcons.ts, ao lado dos SVG. Os "clone" são gerados por cor
    # e não têm SVG próprio.
    source = open(os.path.join(folder, "folderIcons.ts"), encoding="utf-8").read()
    end = source.find("name: 'classic'")
    source = source[:end] if end > 0 else source
    result = {}
    for match in re.finditer(r"\{\s*name:\s*'(folder-[^']+)'", source):
        depth = 0
        i = match.start()
        while True:
            if source[i] in "{[":
                depth += 1
            elif source[i] in "}]":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        body = source[match.start():i + 1]
        names = re.search(r"folderNames:\s*\[(.*?)\]", body, re.S)
        # Os de um pacote só valem com ele ligado, e o padrão do Material é o Angular.
        packs = re.search(r"enabledFor:\s*\[(.*?)\]", body, re.S)
        if packs and "IconPack.Angular" not in packs.group(1):
            continue
        if "clone" not in body and names:
            result[match.group(1)] = re.findall(r"'([^']+)'", names.group(1))
    return result


def circle(element):
    cx, cy, r = (float(element.get(a)) for a in ("cx", "cy", "r"))
    return f"M {cx - r:g} {cy:g} A {r:g} {r:g} 0 1 0 {cx + r:g} {cy:g} A {r:g} {r:g} 0 1 0 {cx - r:g} {cy:g} Z"


def flatten(element, fill, defs, layers):
    tag = element.tag.split("}")[1]
    allowed = {"path": {"d", "fill", "id", "fill-rule", "clip-rule"}, "g": {"fill", "id"}, "use": {XLINK, "href"},
               "circle": {"cx", "cy", "r", "fill"}, "defs": set()}
    if tag not in allowed:
        raise ValueError(f"elemento {tag}")
    for attribute in element.attrib:
        if attribute not in allowed[tag]:
            raise ValueError(f"atributo {attribute} em {tag}")
    fill = element.get("fill", fill)
    if tag == "defs":
        return
    if tag == "use":
        target = (element.get(XLINK) or element.get("href"))[1:]
        flatten(defs[target], fill, defs, layers)
        return
    if tag == "g":
        for child in element:
            flatten(child, fill, defs, layers)
        return
    if fill in (None, "none"):
        return
    if not re.fullmatch(r"#[0-9a-fA-F]{6}", fill):
        raise ValueError(f"cor {fill}")
    data = circle(element) if tag == "circle" else normalize(element.get("d"))
    # F0 é a regra par-ímpar no caminho do Avalonia.
    if element.get("fill-rule") == "evenodd":
        data = "F0 " + data
    layers.append([fill.lower(), data])


def convert_folder(path):
    root = ET.parse(path).getroot()
    if root.get("viewBox") != "0 0 16 16" or set(root.attrib) - {"viewBox"}:
        raise ValueError("caixa ou atributos da raiz")
    defs = {element.get("id"): element for defs in root if defs.tag.endswith("}defs") for element in defs}
    color = None
    motive = []
    for element in root:
        if element.tag.endswith("}defs"):
            continue
        if element.get("id") == "folder":
            if element.get("d") != FOLDER_CLOSED or set(element.attrib) != {"id", "fill", "d"}:
                raise ValueError("pasta fora do formato")
            color = element.get("fill").lower()
        else:
            flatten(element, None, defs, motive)
    if color is None:
        raise ValueError("sem pasta")
    return {"color": color, "motive": motive}


def main():
    folder = sys.argv[1]
    icons = {name: convert(os.path.join(folder, name + ".svg")) for name in ICONS}

    folders = {}
    names = {}
    for icon, folder_list in folder_names(folder).items():
        try:
            folders[icon] = convert_folder(os.path.join(folder, icon + ".svg"))
        except (ValueError, OSError) as error:
            print(f"pasta deixada de fora, {icon}: {error}")
            continue
        for name in folder_list:
            names.setdefault(name.lower(), icon)

    target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icons.json")
    with open(target, "w", encoding="utf-8", newline="\n") as file:
        json.dump({
            "source": "material-extensions/vscode-material-icon-theme " + VERSION,
            "icons": icons,
            "folder": {"color": FOLDER_COLOR, "closed": normalize(FOLDER_CLOSED), "open": normalize(FOLDER_OPEN)},
            "folders": folders,
            "folderNames": names,
        }, file, indent=1)
        file.write("\n")


main()
