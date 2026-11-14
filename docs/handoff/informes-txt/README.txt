==============================================================================
FRAGFORGE — INFORMES EN TEXTO PLANO, CAPAS 5 A 11
==============================================================================

Repositorio : Evo_optimizer_CS2
Version     : 0.1.0
Rama        : arena/c1a5f702-evo-optimizer-cs2
Formato     : .txt sin dependencias. Se abre en Notepad, gedit, less o cat.

------------------------------------------------------------------------------
QUE HAY AQUI
------------------------------------------------------------------------------

  00-informe-integrador-capas5a11.txt
      La vista de conjunto: las siete capas en una tabla, los cinco patrones que
      se repiten, los cambios transversales en Core y los dos hallazgos que mas
      importan. Empieza por aqui si quieres entender que paso en total.

  parte-1-capa5-red.txt          Capa 5 · Red
  parte-2-capa6-igpu.txt         Capa 6 · iGPU Intel y memoria
  parte-3-capa7-latencia.txt     Capa 7 · Latencia
  parte-4-capa8-cs2.txt          Capa 8 · CS2
  parte-5-capa9-sesion.txt       Capa 9 · Modo sesion
  parte-6-capa10-seguridad.txt   Capa 10 · Seguridad
  parte-7-capa11-ui.txt          Capa 11 · UI, instalador, firma, CI y E2E

  Cada parte tiene tres bloques:
    1. Cabecera con commits, rango, ficheros, lineas y tests VERIFICADOS.
    2. Resumen de una pagina: que pedia / que se encontro / que se hizo /
       que se descarto / que NO se verifico.
    3. Transcripcion INTEGRA del informe original de la capa, incluidas sus
       tablas de auditoria archivo:linea y la seccion de tests.

------------------------------------------------------------------------------
DE DONDE SALE ESTO
------------------------------------------------------------------------------

No es un resumen nuevo escrito a mano: cada parte es la conversion a texto
plano del informe .md original (docs/handoff/FragForge-CapaN-Informe.md) con una
cabecera y un resumen anadidos. El conversor solo cambia la PRESENTACION
(encabezados, tablas con pipes, codigo indentado); no recorta contenido.

Si hay discrepancia entre un .txt y su .md, manda el .md: es la fuente.

------------------------------------------------------------------------------
AVISO: NADA DE ESTO SE HA COMPILADO NI EJECUTADO
------------------------------------------------------------------------------

No hay dotnet, ni mono, ni csc, ni PowerShell, ni Inno Setup, ni signtool, ni
certificado, ni Windows en el entorno donde se escribio el proyecto.

  Compilacion ......... NO. Cero builds.
  Tests ............... NO ejecutados. 667 casos existen; ninguno pasado.
  Instalador .......... NO compilado, NO ejecutado.
  Firma ............... NUNCA ejecutada.
  UI WPF .............. NUNCA abierta.
  CI .................. NUNCA ejecutado.

Lo que si se hizo: auditar cada identificador externo con grep contra el
repositorio y citar archivo:linea. Eso descarta errores de sintaxis; NO de tipos
ni de semantica.

Riesgos concretos, en una linea cada uno:
  - Los tests de UI usan el catalogo embebido real y se escribieron sin
    compilador: es donde mas probable es un error de tipos.
  - Un binding roto en WPF no da error, simplemente no muestra nada.
  - El codigo Pascal del instalador se reviso a mano, pero Inno tiene reglas
    propias.

------------------------------------------------------------------------------
ORDEN DE CONSTRUCCION (no es el numerico)
------------------------------------------------------------------------------

Los commits se hicieron en este orden:  8 -> 9 -> 6 -> 7 -> 5 -> 10 -> 11

Por eso el total de tests BAJA al pasar de la capa 5 (614) a la capa 6 (544):
no es un error de la tabla, es el orden real. Las cifras "tests nuevos" de cada
parte estan medidas con git grep en cada commit, no estimadas.

------------------------------------------------------------------------------
CIFRAS DE CIERRE (verificadas en el repo)
------------------------------------------------------------------------------

  Proyectos ................ 8
  Lineas en src/ ........... 16 634
  Lineas en tests/ ......... 12 603
  Tweaks en el catalogo .... 32
  Tweaks opt-in ............ 25 de 32 (solo se aplican por id explicito)
  Casos de test ............ 667 (escritos, no ejecutados)

  Conteo por perfil (recalculado sobre el catalogo real):
      Safe 3 · Balanced 7 · Competitive 9 · Advanced 12

------------------------------------------------------------------------------
RESUMEN ULTRA-CORTO DE LAS SIETE CAPAS
------------------------------------------------------------------------------

  Capa 5   Red.    La ruta de apply identificaba adaptadores POR NOMBRE VISIBLE;
                   ahora por GUID o hardware ID. Dos candidatos de tweak chocaban
                   con la denylist y se descartaron en vez de relajar la politica.
  Capa 6   iGPU.   Los canales de RAM se informan como "probable, no confirmable",
                   NUNCA como "confirmado". Un tweak queda marcado NO VERIFICADO
                   en su propio JSON.
  Capa 7   Latencia. La capa FUE la auditoria: se BAJO la evidencia de
                   mouse-acceleration-off de High a Low. Bajar una cifra ya
                   publicada es mas incomodo que anadir un tweak.
  Capa 8   CS2.    LOS DOS TWEAKS PEDIDOS YA EXISTIAN y hacian lo correcto. Lo que
                   faltaba era resolver cs2.exe, el inspector y la deteccion.
  Capa 9   Sesion. Primero el criterio (efecto inmediato, reversible, sin
                   reinicio), despues el codigo: de 32 tweaks calificaron 3, los
                   otros 29 con motivo escrito.
  Capa 10  Seguridad. HALLAZGO: DefinitionSha256 se escribia en cada snapshot y
                   NADIE lo comprobaba; editar snapshot.json a mano permitia
                   dirigir un revert. Cerrado. Y el journal NO es "firmado": es
                   tamper-evident. La palabra se corrigio.
  Capa 11  UI/CI.  La UI no tenia NI UN TEST porque los ViewModels vivian en un
                   proyecto WPF; se sacaron a FragForge.UI.Core (net10.0, sin
                   WPF). El desinstalador no revierte tweaks ni borra el journal
                   sin preguntar.

==============================================================================
