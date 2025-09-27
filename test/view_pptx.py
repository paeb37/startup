import json, base64

with open('output6.json') as f:
    data = json.load(f)

with open('sanitized6.pptx', 'wb') as f:
    f.write(base64.b64decode(data['export']['pptxBase64']))

print('PPTX saved as sanitized.pptx')