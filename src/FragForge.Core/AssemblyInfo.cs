using System.Runtime.CompilerServices;

// Los tests de FragForge.Tests comprueban piezas que son INTERNAS a proposito: el parseo de la salida de
// PowerShell, la de BitLocker, la estadistica de la sonda de red. No se hacen publicas porque no son API:
// exponerlas obligaria a mantenerlas compatibles para siempre y a documentar detalles que pueden cambiar.
// Esta linea abre el ensamblado SOLO a los tests; el codigo de produccion sigue sin verlas.
[assembly: InternalsVisibleTo("FragForge.Tests")]
