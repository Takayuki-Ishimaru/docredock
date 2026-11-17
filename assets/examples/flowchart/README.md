# Expense approval flowchart sample

The expense approval slide was created for this comparison with editable PowerPoint shapes and connectors. A request leads to an approval decision with two labeled branches. Each converter used the same slide. [Source slide](expense-approval.pptx) · [Comparison image](comparison.png)

| Panel | What it shows |
| --- | --- |
| PowerPoint source | The slide rendered in LibreOffice Impress 24.2.7.2. |
| DocRedock 0.4.0 | The Readable HTML output in Chrome. The SVG redraws the diagram and keeps the arrows and branch labels. |
| [Pandoc 3.9](https://github.com/jgm/pandoc/releases/tag/3.9) | The HTML output in Chrome. The diagram labels appear as separate paragraphs. |
| [MarkItDown 0.1.9](https://github.com/microsoft/markitdown) | The Markdown source. The diagram labels appear on separate lines. |

The screenshots are cropped around the slide and the exported content. In this example, the DocRedock output uses its own colors and placement. The arrows show the same paths as the source slide.

All three tools also exported the slide to Markdown. GitHub renders DocRedock’s Mermaid diagram in the preview. Use **Raw** to see the Markdown source. [DocRedock Markdown](docredock.md) · [Pandoc Markdown](pandoc.md) · [MarkItDown Markdown](markitdown.md)

To reproduce the comparison, run these commands with the listed versions in a working folder containing the source slide. Open the HTML files in a browser and the Markdown files in a text editor or Markdown viewer.

```sh
docredock export expense-approval.pptx --format html --ocr off --content-policy visible -o docredock.html
docredock export expense-approval.pptx --format markdown --lang en --ocr off --content-policy visible -o docredock.md
pandoc --from pptx --to html5 --standalone --embed-resources \
  --metadata 'pagetitle=Expense approval' expense-approval.pptx -o pandoc.html
pandoc --from pptx --to gfm --wrap=none expense-approval.pptx -o pandoc.md
markitdown expense-approval.pptx -o markitdown.md
```

To recreate the input, install `python-pptx==1.0.2` and run `python generate_sample.py expense-approval.pptx`.
