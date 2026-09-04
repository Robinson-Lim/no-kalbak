# GitHub에 처음 업로드하기

대상 저장소는 [Robinson-Lim/no-kalbak](https://github.com/Robinson-Lim/no-kalbak)입니다.
기존 대화에서 생성한 비공개 저장소를 사용합니다.

## 어떤 폴더를 올리나요?

이 `no-kalbak/` 폴더가 소스 저장소입니다.
상위 `df_api/`, 과거 작업 폴더 `fourth/`, 이전 중간 산출물 `github-upload/`, 배포용 `no-kalbak-runtime/`을 함께 올리지 마세요.
실행 파일은 `build.ps1`로 만들며 GitHub에는 Release의 ZIP 첨부 파일로 배포합니다.

## 로그인과 push

로컬 Git 초기화와 `origin` 연결이 준비된 상태에서, 이 폴더의 PowerShell을 열어 확인합니다.

```powershell
git status
git remote -v
git log -1 --oneline
```

Git for Windows의 Git Credential Manager로 로그인합니다.

```powershell
git credential-manager github login --username Robinson-Lim --device
```

화면에 표시된 주소를 브라우저에서 열고 일회용 코드를 입력합니다.
GitHub 비밀번호나 토큰을 대화·소스 파일에 붙여 넣지 마세요.

로그인이 끝나면 다음 명령으로 업로드합니다.

```powershell
git push -u origin main
```

원격 저장소에 이미 다른 커밋이 있다며 거절되면 강제 push하지 말고 먼저 원격 내용을 확인해야 합니다.

## 배포 ZIP 올리기

1. `build.ps1`로 만든 `artifacts/no-kalbak-runtime.zip`을 준비합니다.
2. GitHub 저장소의 Releases에서 새 Release를 만듭니다.
3. 예: 태그 `v0.1.0`, 대상 브랜치 `main`, 개발 중인 버전이면 pre-release로 표시합니다.
4. ZIP과 `no-kalbak-runtime.zip.sha256`을 첨부합니다.

소스만 보관하려면 Release를 만들 필요는 없습니다.
GitHub 자동 생성 Source code ZIP은 실행 파일을 포함하지 않으므로 사용자 실행용 ZIP과 다릅니다.

## 이후 변경 업로드

```powershell
dotnet test .\DnfItemChecker.slnx -c Release
git status
git add .
git diff --cached --stat
git commit -m "Describe the change"
git push
```

개인 설정·DB·캡처와 `artifacts/`는 Git에서 제외됩니다. `.gitignore`를 수정할 때도 이 원칙을 유지하세요.
