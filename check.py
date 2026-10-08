import codecs
with codecs.open('D:/UNI/CAPSTONE/SIGETOUR/SIGETOUR.API/Views/Home/Index.cshtml', 'r', 'utf-8-sig', errors='ignore') as f:
    text = f.read()
    for i, line in enumerate(text.splitlines()):
        if u'\ufffd' in line:
            print(str(i+1) + ': ' + line.strip().replace(u'\ufffd', '[?]'))
