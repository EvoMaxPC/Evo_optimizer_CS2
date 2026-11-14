## 3c. FragForge.Tweaks — catálogo, esquema, localización y validadores

### `src/FragForge.Tweaks/Catalog/Cpu/cpu.power-throttling-off.json`  <sub>(73 líneas)</sub>

````json
{
  "id": "cpu.power-throttling-off",
  "category": "Cpu",
  "title": {
    "es-MX": "Desactivar Power Throttling global",
    "en-US": "Disable global Power Throttling"
  },
  "description": {
    "es-MX": "Aplica la política documentada PowerThrottlingOff=1 en sistemas con CPU híbrida confirmada.",
    "en-US": "Applies the documented PowerThrottlingOff=1 policy on systems with a confirmed hybrid CPU."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "hybridCpu",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling",
            "name": "PowerThrottlingOff",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "cpu",
    "thermal-opt-in"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Bajo/variable",
    "1%lows": "Posible reducción de throttling de fondo; no garantizado",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "Peor: más consumo y temperatura"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-power#powerthrottlingturnoff"
  ],
  "notes": {
    "es-MX": "El i3-1220P es híbrido, pero solo se aplica si EfficiencyClass lo confirma. En un mini PC de 28 W, DTT/IPF sigue gobernando límites térmicos; desactivar throttling puede aumentar temperatura y reducir boost sostenido.",
    "en-US": "The i3-1220P is hybrid, but this only applies if EfficiencyClass confirms it. On a 28 W mini PC, DTT/IPF still owns thermal limits; disabling throttling can raise temperature and reduce sustained boost."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si el equipo ya alcanza límites térmicos, el ventilador es insuficiente o priorizas silencio/consumo. Medir temperatura y 1% lows antes/después.",
    "en-US": "Do not apply if the system already hits thermal limits, cooling is insufficient, or silence/power matter more. Measure temperature and 1% lows before/after."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Cs2/cs2.cpu-priority-high.json`  <sub>(61 líneas)</sub>

````json
{
  "id": "cs2.cpu-priority-high",
  "category": "Cs2",
  "title": {
    "es-MX": "Prioridad alta de CPU para cs2.exe",
    "en-US": "High CPU priority for cs2.exe"
  },
  "description": {
    "es-MX": "Configura IFEO PerfOptions CpuPriorityClass=3 (High) para el nombre de imagen cs2.exe.",
    "en-US": "Configures IFEO PerfOptions CpuPriorityClass=3 (High) for the cs2.exe image name."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "IfeoPerfOptions",
      "parameters": {
        "imageName": "cs2.exe",
        "priorityClass": 3
      }
    }
  ],
  "tags": [
    "competitive",
    "scheduler"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Moderado/variable",
    "1%lows": "Posible mejora si hay competencia de CPU",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/previous-versions/windows/desktop/xperf/image-file-execution-options",
    "https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities"
  ],
  "notes": {
    "es-MX": "No usa Realtime ni afinidad. No inyecta código ni lee memoria de cs2.exe. Puede perjudicar audio, Discord o procesos de fondo si compiten por CPU.",
    "en-US": "Does not use Realtime or affinity. Does not inject code or read cs2.exe memory. May hurt audio, Discord or background processes competing for CPU."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si causa audio entrecortado, peor frametime o inestabilidad. Reiniciar el juego después de aplicar/revertir.",
    "en-US": "Do not apply if it causes crackling audio, worse frametime or instability. Restart the game after apply/revert."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Cs2/cs2.fullscreen-optimizations-off.json`  <sub>(81 líneas)</sub>

````json
{
  "id": "cs2.fullscreen-optimizations-off",
  "category": "Cs2",
  "title": {
    "es-MX": "Desactivar Fullscreen Optimizations para CS2",
    "en-US": "Disable Fullscreen Optimizations for CS2"
  },
  "description": {
    "es-MX": "Añade DISABLEDXMAXIMIZEDWINDOWEDMODE a AppCompatFlags/Layers para la ruta real de cs2.exe, conservando otros flags.",
    "en-US": "Adds DISABLEDXMAXIMIZEDWINDOWEDMODE to AppCompatFlags/Layers for the actual cs2.exe path, preserving other flags."
  },
  "evidence": "Experimental",
  "risk": "Experimental",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    },
    {
      "type": "cs2",
      "parameters": {
        "installed": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers",
            "name": "{Cs2Executable}",
            "kind": "String",
            "value": "DISABLEDXMAXIMIZEDWINDOWEDMODE",
            "mode": "AddToken"
          }
        ]
      }
    }
  ],
  "tags": [
    "opt-in",
    "display",
    "measure-ab"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Desconocido; depende del modo de presentación",
    "1%lows": "Experimental",
    "inputLatency": "Experimental",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/flip-model",
    "https://devblogs.microsoft.com/directx/dxgi-flip-model/"
  ],
  "notes": {
    "es-MX": "El efecto depende de fullscreen exclusivo, ventana sin bordes, flip model y controlador Intel. No es una mejora garantizada. Requiere perfil Advanced y doble confirmación; reiniciar el juego, no Windows.",
    "en-US": "Effect depends on exclusive fullscreen, borderless window, flip model and the Intel driver. Not a guaranteed improvement. Requires Advanced profile and double confirmation; restart the game, not Windows."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si usas ventana sin bordes y el frametime ya es estable, o si empeora alt-tab/overlays. Detectar ruta por Steam o seleccionarla manualmente.",
    "en-US": "Do not apply if borderless frametime is already stable, or if alt-tab/overlays worsen. Detect the path through Steam or select it manually."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Diagnostics/diagnostics.system-inspect.json`  <sub>(69 líneas)</sub>

````json
{
  "id": "diagnostics.system-inspect",
  "category": "Diagnostics",
  "title": {
    "es-MX": "Inspección de sistema de solo lectura",
    "en-US": "Read-only system inspection"
  },
  "description": {
    "es-MX": "Reporta HAGS, MPO, VBS/HVCI, Secure Boot, canales de RAM, PL1/PL2 y salud básica del SSD sin escribir.",
    "en-US": "Reports HAGS, MPO, VBS/HVCI, Secure Boot, RAM channels, PL1/PL2 and basic SSD health without writing."
  },
  "evidence": "High",
  "risk": "Safe",
  "requiresReboot": false,
  "requiresAdmin": false,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "InspectOnly",
      "parameters": {
        "probes": [
          "hags",
          "mpo",
          "vbs",
          "hvci",
          "secureBoot",
          "dualChannel",
          "powerLimits",
          "ssdHealth"
        ]
      }
    }
  ],
  "tags": [
    "diagnostic",
    "read-only"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "None",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows-hardware/drivers/display/hardware-accelerated-gpu-scheduling",
    "https://learn.microsoft.com/en-us/windows/win32/sr/system-restore-wmi-classes"
  ],
  "notes": {
    "es-MX": "Unknown significa que no hay prueba suficiente, no que esté desactivado. Esta acción alimenta la línea base de Capa 1; Apply y Revert no realizan cambios.",
    "en-US": "Unknown means insufficient evidence, not disabled. This action feeds the Layer 1 baseline; Apply and Revert make no changes."
  },
  "whenNotToApply": {
    "es-MX": "No hay riesgo de cambio; puede ejecutarse siempre que WMI/registro estén disponibles.",
    "en-US": "There is no mutation risk; run whenever WMI/registry are available."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.game-dvr-capture-off.json`  <sub>(87 líneas)</sub>

````json
{
  "id": "latency.game-dvr-capture-off",
  "category": "Latency",
  "title": {
    "es-MX": "Desactivar captura Game DVR en segundo plano",
    "en-US": "Disable background Game DVR capture"
  },
  "description": {
    "es-MX": "Desactiva AppCaptureEnabled, GameDVR_Enabled y la política AllowGameDVR sin desinstalar Xbox Game Bar.",
    "en-US": "Disables AppCaptureEnabled, GameDVR_Enabled and the AllowGameDVR policy without uninstalling Xbox Game Bar."
  },
  "evidence": "High",
  "risk": "Moderate",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR",
            "name": "AppCaptureEnabled",
            "kind": "DWord",
            "value": 0,
            "mode": "Set"
          },
          {
            "key": "HKCU\\System\\GameConfigStore",
            "name": "GameDVR_Enabled",
            "kind": "DWord",
            "value": 0,
            "mode": "Set"
          },
          {
            "key": "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\GameDVR",
            "name": "AllowGameDVR",
            "kind": "DWord",
            "value": 0,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "capture",
    "background"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Variable; puede reducir trabajo de captura si estaba activo",
    "1%lows": "Posible mejora modesta si había captura en segundo plano",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-applicationmanagement#allowgamedvr"
  ],
  "notes": {
    "es-MX": "La política bloquea grabación/retransmisión de Windows. Game Bar no se desinstala, pero sus funciones de captura quedan deshabilitadas. No toca Defender, Firewall ni Update.",
    "en-US": "The policy blocks Windows recording/broadcasting. Game Bar is not uninstalled, but its capture features are disabled. Defender, Firewall and Update are untouched."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si usas Game Bar para clips, grabación o streaming. Si ya está desactivado por Atlas, se marcará AppliedExternally y no se sobrescribirá.",
    "en-US": "Do not apply if you use Game Bar for clips, recording or streaming. If Atlas already disabled it, it is marked AppliedExternally and not overwritten."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.game-mode-on.json`  <sub>(79 líneas)</sub>

````json
{
  "id": "latency.game-mode-on",
  "category": "Latency",
  "title": {
    "es-MX": "Activar Modo de juego de Windows",
    "en-US": "Enable Windows Game Mode"
  },
  "description": {
    "es-MX": "Activa AutoGameModeEnabled y AllowAutoGameMode para el usuario interactivo.",
    "en-US": "Enables AutoGameModeEnabled and AllowAutoGameMode for the interactive user."
  },
  "evidence": "Medium",
  "risk": "Safe",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Software\\Microsoft\\GameBar",
            "name": "AutoGameModeEnabled",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          },
          {
            "key": "HKCU\\Software\\Microsoft\\GameBar",
            "name": "AllowAutoGameMode",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "scheduling"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Variable, generalmente pequeño",
    "1%lows": "Posible mejora de consistencia en equipos con carga de fondo",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://support.xbox.com/es-ES/help/games-apps/game-setup-and-play/use-game-mode-for-best-gaming-experience"
  ],
  "notes": {
    "es-MX": "Game Mode prioriza el juego frente a tareas de fondo; no es un multiplicador de FPS. El beneficio depende de la carga y del reconocimiento del juego.",
    "en-US": "Game Mode prioritizes the game over background work; it is not an FPS multiplier. Benefit depends on load and game recognition."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si una prueba A/B muestra peor frametime o si una herramienta de gestión ya fuerza otra política equivalente.",
    "en-US": "Do not apply if an A/B test shows worse frametime or another management tool already enforces an equivalent policy."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.global-timer-resolution.json`  <sub>(68 líneas)</sub>

````json
{
  "id": "latency.global-timer-resolution",
  "category": "Latency",
  "title": {
    "es-MX": "Solicitudes globales de resolución de timer",
    "en-US": "Global timer resolution requests"
  },
  "description": {
    "es-MX": "Establece GlobalTimerResolutionRequests=1 para solicitar el comportamiento global previo a Windows 10 2004.",
    "en-US": "Sets GlobalTimerResolutionRequests=1 to request the pre-Windows 10 2004 global behavior."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel",
            "name": "GlobalTimerResolutionRequests",
            "kind": "DWord",
            "value": 1,
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "timer",
    "measure-ab"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "No garantizado",
    "inputLatency": "Variable; puede afectar esperas/temporización",
    "network": "None",
    "boot": "None",
    "thermals": "Peor: posible mayor consumo"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod",
    "https://learn.microsoft.com/en-us/windows/win32/directshow/ireferenceclocktimercontrol"
  ],
  "notes": {
    "es-MX": "La documentación oficial describe timeBeginPeriod y el cambio de Windows 10 2004/Windows 11; esta clave concreta no está documentada como contrato público. Evidence Medium se refiere al mecanismo del timer, no a una ganancia probada en CS2. Puede aumentar interrupciones/consumo.",
    "en-US": "Official docs describe timeBeginPeriod and the Windows 10 2004/Windows 11 change; this specific value is not a documented public contract. Medium evidence refers to the timer mechanism, not a proven CS2 gain. It may increase interrupts/power."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar sin línea base A/B. Evitar si aumenta temperatura, consumo o DPC/ISR, o si el juego ya solicita resolución adecuada.",
    "en-US": "Do not apply without an A/B baseline. Avoid if temperature, power or DPC/ISR rise, or if the game already requests an adequate resolution."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Latency/latency.mouse-acceleration-off.json`  <sub>(102 líneas)</sub>

````json
{
  "id": "latency.mouse-acceleration-off",
  "category": "Latency",
  "title": {
    "es-MX": "Desactivar aceleración del mouse del escritorio",
    "en-US": "Disable desktop mouse acceleration"
  },
  "description": {
    "es-MX": "Establece MouseSpeed y umbrales en 0 y curvas lineales 1:1 en el hive del usuario interactivo.",
    "en-US": "Sets MouseSpeed and thresholds to 0 and linear 1:1 curves in the interactive user hive."
  },
  "evidence": "High",
  "risk": "Safe",
  "requiresReboot": true,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "userHive",
      "parameters": {
        "expected": true
      }
    }
  ],
  "actions": [
    {
      "kind": "Registry",
      "parameters": {
        "values": [
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "MouseSpeed",
            "kind": "String",
            "value": "0",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "MouseThreshold1",
            "kind": "String",
            "value": "0",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "MouseThreshold2",
            "kind": "String",
            "value": "0",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "SmoothMouseXCurve",
            "kind": "Binary",
            "value": "AAAAAAAAAAAAAAEAAAAAAAAAAgAAAAAAAAADAAAAAAAAAAQAAAAAAA==",
            "mode": "Set"
          },
          {
            "key": "HKCU\\Control Panel\\Mouse",
            "name": "SmoothMouseYCurve",
            "kind": "Binary",
            "value": "AAAAAAAAAAAAAAEAAAAAAAAAAgAAAAAAAAADAAAAAAAAAAQAAAAAAA==",
            "mode": "Set"
          }
        ]
      }
    }
  ],
  "tags": [
    "input",
    "desktop-wide"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "None",
    "inputLatency": "Consistencia del puntero; no garantiza menor latencia en CS2 con raw input",
    "network": "None",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfoa",
    "https://fleexlab.blogspot.com/2020/08/setting-arbitrarily-precise-mouse.html"
  ],
  "notes": {
    "es-MX": "Afecta a todo el escritorio y a aplicaciones que usan el puntero de Windows. CS2 puede usar raw input y no verse afectado. Los cambios de registro requieren cerrar sesión/reiniciar; no se inyecta ni se modifica cs2.exe.",
    "en-US": "Affects the entire desktop and applications using the Windows pointer. CS2 may use raw input and be unaffected. Registry changes require sign-out/reboot; cs2.exe is not injected or modified."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si dependes de Precisión mejorada del puntero o de una curva personalizada. Guarda tu configuración actual; la reversión restaura bytes exactos.",
    "en-US": "Do not apply if you rely on Enhance pointer precision or a custom curve. The revert restores exact bytes."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Network/network.i225v-eee-off.json`  <sub>(76 líneas)</sub>

````json
{
  "id": "network.i225v-eee-off",
  "category": "Network",
  "title": {
    "es-MX": "Desactivar EEE en Intel I225-V",
    "en-US": "Disable EEE on Intel I225-V"
  },
  "description": {
    "es-MX": "Desactiva la propiedad avanzada Energy Efficient Ethernet (*EEE) si el controlador la expone exactamente.",
    "en-US": "Disables the Energy Efficient Ethernet (*EEE) advanced property if the driver exposes it exactly."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    },
    {
      "type": "adapter",
      "parameters": {
        "descriptionContains": "I225-V",
        "physicalOnly": true
      }
    }
  ],
  "actions": [
    {
      "kind": "NetAdapterProperty",
      "parameters": {
        "adapterName": "{AdapterDescription:I225-V}",
        "properties": [
          {
            "keyword": "*EEE",
            "value": [
              "0"
            ]
          }
        ]
      }
    }
  ],
  "tags": [
    "competitive",
    "opt-in",
    "network"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "None",
    "1%lows": "None",
    "inputLatency": "None",
    "network": "Posible reducción de latencia de salida de baja potencia; no aumenta FPS",
    "boot": "None",
    "thermals": "None"
  },
  "references": [
    "https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapteradvancedproperty",
    "https://learn.microsoft.com/en-us/windows-hardware/drivers/network/energy-efficient-ethernet"
  ],
  "notes": {
    "es-MX": "Solo I225-V; no toca Wi-Fi ni otros adaptadores. Puede renegociar el enlace y causar una interrupción breve. Si la palabra clave no existe, el tweak es NotApplicable.",
    "en-US": "I225-V only; Wi-Fi and other adapters are untouched. It may renegotiate the link and cause a brief interruption. If the keyword is absent, the tweak is NotApplicable."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar durante una partida o si el enlace es inestable. No esperar menor ping a Internet; el efecto es local al enlace.",
    "en-US": "Do not apply during a match or if the link is unstable. Do not expect lower Internet ping; the effect is local to the link."
  }
}
````

### `src/FragForge.Tweaks/Catalog/Power/power.fragforge-performance.json`  <sub>(77 líneas)</sub>

````json
{
  "id": "power.fragforge-performance",
  "category": "Power",
  "title": {
    "es-MX": "Plan FragForge Performance",
    "en-US": "FragForge Performance plan"
  },
  "description": {
    "es-MX": "Duplica Alto rendimiento, fija procesador mínimo 100% y CPMINCORES 100% en AC/DC, y activa el plan nuevo.",
    "en-US": "Duplicates High performance, sets minimum processor 100% and CPMINCORES 100% on AC/DC, and activates the new plan."
  },
  "evidence": "Medium",
  "risk": "Moderate",
  "requiresReboot": false,
  "requiresAdmin": true,
  "conditions": [
    {
      "type": "windows",
      "parameters": {
        "minBuild": 26100,
        "maxBuild": 26299,
        "architecture": "X64"
      }
    }
  ],
  "actions": [
    {
      "kind": "PowerPlan",
      "parameters": {
        "planName": "FragForge Performance",
        "sourceSchemeGuid": "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        "activate": true,
        "settings": [
          {
            "subgroupGuid": "54533251-82be-4824-96c1-47b60b740d00",
            "settingGuid": "893dee8e-2bef-41e0-89c6-b55d0929964c",
            "acValue": 100,
            "dcValue": 100
          },
          {
            "subgroupGuid": "54533251-82be-4824-96c1-47b60b740d00",
            "settingGuid": "0cc5b647-c1df-4637-891a-dec35c318583",
            "acValue": 100,
            "dcValue": 100
          }
        ]
      }
    }
  ],
  "tags": [
    "competitive",
    "opt-in",
    "thermal-opt-in"
  ],
  "conflictsWith": [],
  "dependsOn": [],
  "expectedImpact": {
    "fps": "Variable",
    "1%lows": "Posible mejora de consistencia; puede empeorar por temperatura",
    "inputLatency": "None",
    "network": "None",
    "boot": "None",
    "thermals": "Peor: más consumo/reposo caliente"
  },
  "references": [
    "https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options",
    "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/powercfg"
  ],
  "notes": {
    "es-MX": "No borra planes existentes. Si el plan ya existe, captura sus ajustes antes de modificarlo. En el PN64, DTT/IPF y PL1/PL2 siguen mandando; 100% mínimo no elimina límites térmicos. Revert reactiva el plan anterior y elimina solo el plan creado.",
    "en-US": "Does not delete existing plans. If the plan already exists, its settings are captured before modification. On the PN64, DTT/IPF and PL1/PL2 still dominate; 100% minimum does not remove thermal limits. Revert reactivates the previous plan and deletes only the created plan."
  },
  "whenNotToApply": {
    "es-MX": "No aplicar si el mini PC se calienta demasiado, hace ruido o reduce clocks bajo carga sostenida. No es recomendable para batería.",
    "en-US": "Do not apply if the mini PC overheats, is noisy, or clocks down under sustained load. Not recommended on battery."
  }
}
````

### `src/FragForge.Tweaks/FragForge.Tweaks.csproj`  <sub>(11 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../FragForge.Core/FragForge.Core.csproj" />
    <EmbeddedResource Include="Catalog/**/*.json" />
    <EmbeddedResource Include="Localization/*.json" />
    <EmbeddedResource Include="Schema/*.json" />
    <None Include="Catalog/**/*.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Catalog/%(RecursiveDir)%(Filename)%(Extension)" />
    <None Include="Localization/*.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Localization/%(Filename)%(Extension)" />
    <None Include="Schema/*.json" CopyToOutputDirectory="PreserveNewest" TargetPath="Schema/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Tweaks/Localization/LocalizationLoader.cs`  <sub>(15 líneas)</sub>

````csharp
using FragForge.Core.Localization;

namespace FragForge.Tweaks.Localization;

public static class LocalizationLoader
{
    public static async Task<Localizer> LoadAsync(string culture, CancellationToken cancellationToken = default)
    {
        var normalized = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-MX";
        var resource = $"FragForge.Tweaks.Localization.{normalized}.json";
        using var stream = typeof(LocalizationLoader).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException($"Localization resource '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return Localizer.FromJson(normalized, await reader.ReadToEndAsync(cancellationToken));
    }
}
````

### `src/FragForge.Tweaks/Localization/en-US.json`  <sub>(37 líneas)</sub>

````json
{
  "app.title": "FragForge — Layer 0",
  "nav.scan": "Scan",
  "nav.apply": "Apply selected",
  "nav.revert": "Revert",
  "nav.dryRun": "Dry-run",
  "nav.snapshot": "Create snapshot",
  "nav.export": "Export report",
  "hardware.title": "Detected hardware",
  "tweaks.title": "Tweaks",
  "logs.title": "Log",
  "profile.label": "Profile",
  "culture.label": "Language",
  "state.NotApplied": "Not applied",
  "state.Applied": "Applied",
  "state.PartiallyApplied": "Partial",
  "state.NotApplicable": "Not applicable",
  "state.Unknown": "Unknown",
  "state.AppliedExternally": "Applied externally",
  "state.Inspected": "Read-only",
  "risk.title": "Risk confirmation",
  "risk.first": "This tweak may reduce stability or security. Continue?",
  "risk.second": "Confirm again. Type ACEPTO EL RIESGO to enable application.",
  "risk.phrase": "ACEPTO EL RIESGO",
  "busy": "Operation in progress…",
  "ready": "Ready",
  "error": "Error",
  "cancel": "Cancel",
  "selected": "Selected",
  "cs2Path": "Manual cs2.exe path",
  "restorePoint": "Create restore point",
  "enableProtection": "Enable System Protection if turned off",
  "force": "Force overwrite of external state",
  "allowPowerShell": "Allow experimental PowerShell actions",
  "doctor.title": "Preflight",
  "journal.integrity": "Journal integrity"
}
````

### `src/FragForge.Tweaks/Localization/es-MX.json`  <sub>(37 líneas)</sub>

````json
{
  "app.title": "FragForge — Capa 0",
  "nav.scan": "Escanear",
  "nav.apply": "Aplicar seleccionados",
  "nav.revert": "Revertir",
  "nav.dryRun": "Dry-run",
  "nav.snapshot": "Crear snapshot",
  "nav.export": "Exportar informe",
  "hardware.title": "Hardware detectado",
  "tweaks.title": "Tweaks",
  "logs.title": "Registro",
  "profile.label": "Perfil",
  "culture.label": "Idioma",
  "state.NotApplied": "No aplicado",
  "state.Applied": "Aplicado",
  "state.PartiallyApplied": "Parcial",
  "state.NotApplicable": "No aplicable",
  "state.Unknown": "Desconocido",
  "state.AppliedExternally": "Aplicado externamente",
  "state.Inspected": "Informativo",
  "risk.title": "Confirmación de riesgo",
  "risk.first": "Este tweak puede reducir estabilidad o seguridad. ¿Deseas continuar?",
  "risk.second": "Confirma de nuevo. Escribe ACEPTO EL RIESGO para habilitar la aplicación.",
  "risk.phrase": "ACEPTO EL RIESGO",
  "busy": "Operación en curso…",
  "ready": "Listo",
  "error": "Error",
  "cancel": "Cancelar",
  "selected": "Seleccionados",
  "cs2Path": "Ruta manual de cs2.exe",
  "restorePoint": "Crear punto de restauración",
  "enableProtection": "Habilitar protección del sistema si está apagada",
  "force": "Forzar sobrescritura de estado externo",
  "allowPowerShell": "Permitir acciones PowerShell experimentales",
  "doctor.title": "Preflight",
  "journal.integrity": "Integridad del journal"
}
````

### `src/FragForge.Tweaks/Schema/tweak.schema.json`  <sub>(47 líneas)</sub>

````json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://fragforge.local/schemas/tweak-0.1.json",
  "title": "FragForge Tweak Definition v1",
  "type": "object",
  "additionalProperties": false,
  "required": ["id", "category", "title", "description", "evidence", "risk", "expectedImpact", "actions", "references"],
  "properties": {
    "id": { "type": "string", "minLength": 3, "pattern": "^[a-z0-9][a-z0-9.-]{2,80}$" },
    "category": { "type": "string", "enum": ["Cpu", "Power", "Network", "Gpu", "Latency", "Cs2", "Debloat", "Memory", "Diagnostics"] },
    "title": { "$ref": "#/$defs/localizedText" },
    "description": { "$ref": "#/$defs/localizedText" },
    "evidence": { "type": "string", "enum": ["High", "Medium", "Low", "Experimental"] },
    "risk": { "type": "string", "enum": ["Safe", "Moderate", "Advanced", "Experimental"] },
    "requiresReboot": { "type": "boolean" },
    "requiresAdmin": { "type": "boolean" },
    "conditions": { "type": "array", "items": { "$ref": "#/$defs/condition" } },
    "actions": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/action" } },
    "tags": { "type": "array", "items": { "type": "string", "minLength": 1 } },
    "conflictsWith": { "type": "array", "items": { "type": "string", "minLength": 1 } },
    "dependsOn": { "type": "array", "items": { "type": "string", "minLength": 1 } },
    "expectedImpact": {
      "type": "object", "additionalProperties": false,
      "properties": {
        "fps": { "type": "string" }, "1%lows": { "type": "string" }, "inputLatency": { "type": "string" },
        "network": { "type": "string" }, "boot": { "type": "string" }, "thermals": { "type": "string" }
      }
    },
    "references": { "type": "array", "minItems": 1, "items": { "type": "string", "minLength": 1 } },
    "notes": { "$ref": "#/$defs/localizedText" },
    "whenNotToApply": { "$ref": "#/$defs/localizedText" }
  },
  "$defs": {
    "localizedText": {
      "type": "object", "additionalProperties": false, "required": ["es-MX", "en-US"],
      "properties": { "es-MX": { "type": "string", "minLength": 1 }, "en-US": { "type": "string", "minLength": 1 } }
    },
    "condition": {
      "type": "object", "additionalProperties": false, "required": ["type"],
      "properties": { "type": { "type": "string", "minLength": 1 }, "parameters": { "type": "object" } }
    },
    "action": {
      "type": "object", "additionalProperties": false, "required": ["kind"],
      "properties": { "kind": { "type": "string", "minLength": 1 }, "parameters": { "type": "object" } }
    }
  }
}
````

### `src/FragForge.Tweaks/TweakCatalog.cs`  <sub>(68 líneas)</sub>

````csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Tweaks.Validation;

namespace FragForge.Tweaks;

public sealed class TweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyDictionary<string, TweakDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, string> _hashes;
    public IReadOnlyList<TweakDefinition> Definitions { get; }
    public TweakCatalog(IReadOnlyList<TweakDefinition> definitions)
    {
        Definitions = definitions.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        _definitions = Definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _hashes = Definitions.ToDictionary(d => d.Id, d => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d, JsonDefaults.Create(false))))).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }
    public TweakDefinition Get(string id) => _definitions.TryGetValue(id, out var definition) ? definition : throw new KeyNotFoundException($"Tweak desconocido: {id}");
    public bool TryGet(string id, out TweakDefinition? definition) => _definitions.TryGetValue(id, out definition);
    public string GetDefinitionHash(string id) => _hashes[id];
}
public sealed class JsonTweakCatalogLoader(TweakCatalogValidator validator)
{
    public static string ReadEmbeddedSchema()
    {
        using var stream = typeof(JsonTweakCatalogLoader).Assembly.GetManifestResourceStream("FragForge.Tweaks.Schema.tweak.schema.json") ?? throw new InvalidDataException("Embedded tweak schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public async Task<TweakCatalog> LoadEmbeddedAsync(string? additionalDirectory = null, CancellationToken cancellationToken = default)
    {
        var assembly = typeof(JsonTweakCatalogLoader).Assembly;
        var documents = new List<(string Name, string Json)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("FragForge.Tweaks.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            documents.Add((resource, await reader.ReadToEndAsync(cancellationToken)));
        }
        if (additionalDirectory is not null && Directory.Exists(additionalDirectory))
            foreach (var file in Directory.GetFiles(additionalDirectory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)) documents.Add((file, await File.ReadAllTextAsync(file, cancellationToken)));
        return LoadDocuments(documents);
    }
    public TweakCatalog LoadDocuments(IReadOnlyList<(string Name, string Json)> documents)
    {
        var definitions = new List<TweakDefinition>();
        var errors = new List<string>();
        foreach (var (name, json) in documents)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var structural = validator.ValidateStructure(document.RootElement);
                if (structural.Count > 0) { errors.AddRange(structural.Select(e => $"{name}: {e}")); continue; }
                var definition = JsonSerializer.Deserialize<TweakDefinition>(json, JsonDefaults.Options) ?? throw new JsonException("Documento nulo.");
                definitions.Add(definition);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or CatalogValidationException) { errors.Add($"{name}: {ex.Message}"); }
        }
        errors.AddRange(validator.ValidateCatalog(definitions));
        if (errors.Count > 0) throw new CatalogValidationException("El catálogo no pasó la validación de esquema/semántica.", errors);
        return new TweakCatalog(definitions);
    }
}
````

### `src/FragForge.Tweaks/Validation/JsonSchemaValidator.cs`  <sub>(76 líneas)</sub>

````csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FragForge.Tweaks.Validation;

public sealed class JsonSchemaValidator
{
    private readonly JsonElement _schema;
    public JsonSchemaValidator(string schemaJson) => _schema = JsonDocument.Parse(schemaJson).RootElement.Clone();
    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Validate(instance, _schema, "$", errors, 0);
        return errors;
    }
    private void Validate(JsonElement instance, JsonElement schema, string path, List<string> errors, int depth)
    {
        if (depth > 64) { errors.Add($"{path}: profundidad máxima excedida"); return; }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var pointer = reference.GetString()!;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) { errors.Add($"{path}: solo se admiten referencias locales"); return; }
            var resolved = _schema;
            foreach (var segment in pointer[2..].Split('/')) resolved = resolved.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
            Validate(instance, resolved, path, errors, depth + 1);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var expected = type.GetString();
            var valid = expected switch
            {
                "object" => instance.ValueKind == JsonValueKind.Object,
                "array" => instance.ValueKind == JsonValueKind.Array,
                "string" => instance.ValueKind == JsonValueKind.String,
                "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
                "number" => instance.ValueKind == JsonValueKind.Number,
                _ => true
            };
            if (!valid) { errors.Add($"{path}: se esperaba {expected}, se obtuvo {instance.ValueKind}"); return; }
        }
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(e => JsonElement.DeepEquals(e, instance))) errors.Add($"{path}: valor fuera de enum");
        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)) foreach (var property in required.EnumerateArray()) if (!instance.TryGetProperty(property.GetString()!, out _)) errors.Add($"{path}: falta '{property.GetString()}'");
            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            var additional = !schema.TryGetProperty("additionalProperties", out var a) || a.ValueKind != JsonValueKind.False;
            foreach (var property in instance.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child)) Validate(property.Value, child, path + "." + property.Name, errors, depth + 1);
                else if (!additional) errors.Add($"{path}: propiedad no permitida '{property.Name}'");
            }
        }
        if (instance.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min) && instance.GetArrayLength() < min.GetInt32()) errors.Add($"{path}: menos de {min.GetInt32()} elementos");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in instance.EnumerateArray()) Validate(item, items, $"{path}[{index++}]", errors, depth + 1);
            }
        }
        if (instance.ValueKind == JsonValueKind.String)
        {
            var value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32()) errors.Add($"{path}: longitud mínima {min.GetInt32()}");
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) errors.Add($"{path}: no cumple el patrón");
        }
        if (instance.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var min) && instance.GetDouble() < min.GetDouble()) errors.Add($"{path}: menor que mínimo");
            if (schema.TryGetProperty("maximum", out var max) && instance.GetDouble() > max.GetDouble()) errors.Add($"{path}: mayor que máximo");
        }
    }
}
````

### `src/FragForge.Tweaks/Validation/TweakCatalogValidator.cs`  <sub>(52 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Models;

namespace FragForge.Tweaks.Validation;

public sealed class TweakCatalogValidator(JsonSchemaValidator schema, ITweakConditionFactory conditions, ITweakActionFactory actions)
{
    public IReadOnlyList<string> ValidateStructure(JsonElement document) => schema.Validate(document);
    public IReadOnlyList<string> ValidateCatalog(IReadOnlyList<TweakDefinition> definitions)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!ids.Add(definition.Id)) errors.Add($"ID duplicado: {definition.Id}");
            if (definition.Title.EsMx.Length == 0 || definition.Title.EnUs.Length == 0 || definition.Description.EsMx.Length == 0 || definition.Description.EnUs.Length == 0) errors.Add($"{definition.Id}: textos localizados vacíos");
            if (definition.Actions.Count == 0) errors.Add($"{definition.Id}: sin acciones");
            if (!definition.ReadOnly && !definition.RequiresAdmin) errors.Add($"{definition.Id}: una acción mutante requiere Admin");
            if (definition.References.Count == 0) errors.Add($"{definition.Id}: sin referencias");
            if (definition.DependsOn.Contains(definition.Id, StringComparer.OrdinalIgnoreCase) || definition.ConflictsWith.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)) errors.Add($"{definition.Id}: dependencia/conflicto consigo mismo");
            foreach (var condition in definition.Conditions) { try { conditions.Create(condition); } catch (Exception ex) { errors.Add($"{definition.Id}: condición inválida: {ex.Message}"); } }
            foreach (var action in definition.Actions)
            {
                try { actions.Create(action); } catch (Exception ex) { errors.Add($"{definition.Id}: acción inválida: {ex.Message}"); }
                if (action.Kind.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || action.Kind.Equals("Bcdedit", StringComparison.OrdinalIgnoreCase))
                    if (definition.Risk is not (RiskLevel.Advanced or RiskLevel.Experimental)) errors.Add($"{definition.Id}: {action.Kind} requiere riesgo Advanced/Experimental");
            }
        }
        foreach (var definition in definitions)
        {
            foreach (var dependency in definition.DependsOn) if (!ids.Contains(dependency)) errors.Add($"{definition.Id}: dependencia inexistente {dependency}");
            foreach (var conflict in definition.ConflictsWith) if (!ids.Contains(conflict)) errors.Add($"{definition.Id}: conflicto inexistente {conflict}");
        }
        var catalog = new InMemoryCatalog(definitions);
        foreach (var definition in definitions)
        {
            try { DependencyResolver.Resolve([definition.Id], catalog); }
            catch (FragForgeException ex) { errors.Add($"{definition.Id}: {ex.Message}"); }
        }
        return errors;
    }
    private sealed class InMemoryCatalog(IReadOnlyList<TweakDefinition> definitions) : ITweakCatalog
    {
        private readonly IReadOnlyDictionary<string, TweakDefinition> _map = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<TweakDefinition> Definitions => definitions;
        public TweakDefinition Get(string id) => _map[id];
        public bool TryGet(string id, out TweakDefinition? definition) => _map.TryGetValue(id, out definition);
        public string GetDefinitionHash(string id) => "";
    }
}
````

