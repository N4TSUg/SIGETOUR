import codecs
with codecs.open('D:/UNI/CAPSTONE/SIGETOUR/SIGETOUR.API/Views/Home/Index.cshtml', 'r', 'utf-8-sig', errors='ignore') as f:
    text = f.read()

text = text.replace('Guti\ufffdrrez', 'Gutiérrez')
text = text.replace('Rodr\ufffdguez', 'Rodríguez')
text = text.replace('2025 \ufffd Jr', '2025 • Jr')
text = text.replace('PM \ufffd 3,500', 'PM • 3,500')
text = text.replace('Lima \ufffd Viaje', 'Lima • Viaje')
text = text.replace('Trujillo \ufffd Servicio', 'Trujillo • Servicio')
text = text.replace('USA \ufffd Cumbemayo', 'USA • Cumbemayo')

with codecs.open('D:/UNI/CAPSTONE/SIGETOUR/SIGETOUR.API/Views/Home/Index.cshtml', 'w', 'utf-8-sig') as f:
    f.write(text)
print('Fixed remaining')
