# 배포 검증 · 1.0.2 · 2026-09-05

대상은 1.0.2 소스와 Windows x64 self-contained 배포 ZIP입니다.

## 자동 테스트

`dotnet test .\DnfItemChecker.slnx -c Release --no-restore` 결과:

| 프로젝트 | 통과 | 실패 |
|---|---:|---:|
| Core | 91 | 0 |
| App | 6 | 0 |
| Vision | 41 | 0 |

총 **138/138 통과**했습니다. 전체 솔루션 재실행과 Vision 프로젝트 단독 실행에서도 동일하게 통과했습니다.

이전 검증에서 GDI+ BMP 인코더 초기화가 한 차례 실패해, 합성 이미지를 생성하는 Vision 테스트 클래스를 순차 실행하도록 격리했습니다. `OcrConcurrencyTests` 내부의 병렬·취소·종료 시나리오는 그대로 유지합니다.

## 의존성 감사

`Microsoft.Data.Sqlite` 10.0.9가 가져오던 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 대신 2.1.13을 직접 지정했습니다. `dotnet list .\DnfItemChecker.slnx package --include-transitive --vulnerable` 결과 6개 프로젝트 모두 알려진 취약 패키지 0개입니다.

## 빌드·패키지

- `build.ps1`의 Release / win-x64 / self-contained 게시와 ZIP 생성 성공.
- 빈 API 키 설정 예제, 실행 파일, ONNX 모델 3개, 한국어 사전, 기준표, 실행 안내로 배포 구성.
- 그래프 JSON과 1.0.1 측정 결과의 핵심 수치 일치, 문서 상대 링크 확인.
- 과거 143장 인식 결과는 2026-09-04 측정값이며, 1.0.2 변경에서 OCR 판독 로직은 바뀌지 않았습니다.

## 추가 검증 범위

장시간 실제 게임 사용과 다양한 DPI·해상도에서의 캡처부터 화면 표시까지의 응답 시간은 향후 성능 검증 대상입니다.
