using System.Runtime.CompilerServices;

// Ver el equivalente en FragForge.Core/AssemblyInfo.cs. Aqui viven el parseo de libraryfolders.vdf y del
// appmanifest (SteamCs2Resolver), el de Get-NetAdapterAdvancedProperty (NetworkAdvancedPropertyReader) y la
// mediana/percentil de la sonda de red: internos a proposito, abiertos solo al ensamblado de tests.
[assembly: InternalsVisibleTo("FragForge.Tests")]
