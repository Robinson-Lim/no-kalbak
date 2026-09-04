# 인식 벤치마크

`tools/RecogProbe`는 원본 `fourth/tools/RecogProbe`에서 가져온 검증 도구입니다.
게임 캡처와 정답 데이터는 저장소에 포함하지 않습니다. 기존 `testdataset/`을 별도로 보관하세요.

## 빌드와 실행

저장소 루트에서 실행합니다. 기본 hybrid 경로는 balanced 결과를 측정합니다.

```powershell
dotnet build .\tools\RecogProbe\RecogProbe.csproj -c Release
New-Item -ItemType Directory -Path .\artifacts -Force
dotnet run --project .\tools\RecogProbe\RecogProbe.csproj -c Release --no-build -- --labels ..\testdataset\dataset\benchmark\labels_all.jsonl --report .\artifacts\benchmark-all.json --failure-report .\artifacts\benchmark-failures.json
```

`--labels`에 지정한 JSONL이 참조하는 이미지 경로도 유효해야 합니다. 다른 PC에서는 데이터셋 위치에 맞게 경로를 준비하세요.
원본 데이터를 수정할 필요는 없습니다. `--immediate`는 미완성 결과를 반환할 수 있는 별도 경로이므로 balanced 정확도·지연과 혼동하면 안 됩니다.

라이브 캡처 검증:

```powershell
dotnet run --project .\tools\RecogProbe\RecogProbe.csproj -c Release -- --validate-live ..\fourth\debug_captures --profiles .\tools\RecogProbe\live-acceptance.json --min-trials 30
```

해당 경로들은 기존 작업 폴더 배치를 기준으로 한 예시입니다. 다른 PC에서는 외부 데이터 경로를 지정하세요.

## 보관된 기준 결과

`docs/benchmarks/`의 JSON은 기존 최종 측정 결과를 복사하고 개인 PC의 절대 경로를 제거한 것입니다.
이번 정리 작업에서 다시 측정한 값은 [검토 기록](REVIEW.md)에 따로 표시합니다.

| 기존 결과 | 핵심 필드 | 능력치 값 | 평균 | P95 |
|---|---:|---:|---:|---:|
| 전체 143장 | 143/143 | 176/176 | 약 485ms | 약 567ms |
| frozen test 29장 | 29/29 | 29/29 | JSON 참조 | JSON 참조 |

아이템 이름은 핵심 필드 평가에서 제외되어 있습니다.
파일 읽기 비용은 포함하지만 실제 화면 캡처·커서 대기·WPF 렌더링 전체 시간은 포함하지 않습니다.
