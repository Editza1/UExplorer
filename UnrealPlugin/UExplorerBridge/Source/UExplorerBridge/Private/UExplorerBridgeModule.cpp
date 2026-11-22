#include "UExplorerBridgeModule.h"
#include "UExplorerAssetImporter.h"
#include "UExplorerShaderTranslator.h"
#include "HttpServerModule.h"
#include "IHttpRouter.h"
#include "HttpServerRequest.h"
#include "HttpServerResponse.h"
#include "ToolMenus.h"
#include "Misc/Paths.h"
#include "Misc/FileHelper.h"
#include "HAL/FileManager.h"
#include "Misc/ScopeLock.h"

DEFINE_LOG_CATEGORY_STATIC(LogUExplorerBridge, Log, All);

static FString GetBridgeQueueDirectory()
{
	FString LocalAppData = FPlatformMisc::GetEnvironmentVariable(TEXT("LOCALAPPDATA"));
	if (LocalAppData.IsEmpty())
	{
		LocalAppData = FPaths::ProjectSavedDir();
	}
	return FPaths::Combine(LocalAppData, TEXT("UExplorer"), TEXT("UnrealBridge"), TEXT("Queue"));
}

void FUExplorerBridgeModule::StartupModule()
{
	UE_LOG(LogUExplorerBridge, Log, TEXT("UExplorerBridge module starting (port %u)..."), BridgePort);
	StartHttpListener();

	TickerHandle = FTSTicker::GetCoreTicker().AddTicker(
		FTickerDelegate::CreateRaw(this, &FUExplorerBridgeModule::OnTickPollQueue),
		0.5f);

	UToolMenus::RegisterStartupCallback(FSimpleMulticastDelegate::FDelegate::CreateRaw(this, &FUExplorerBridgeModule::RegisterMenus));
}

void FUExplorerBridgeModule::ShutdownModule()
{
	if (TickerHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickerHandle);
		TickerHandle.Reset();
	}
	StopHttpListener();
	UToolMenus::UnRegisterStartupCallback(this);
	UToolMenus::UnregisterOwner(this);
}

void FUExplorerBridgeModule::RegisterMenus()
{
	FToolMenuOwnerScoped OwnerScoped(this);
	if (UToolMenu* ToolbarMenu = UToolMenus::Get()->ExtendMenu("LevelEditor.LevelEditorToolBar.PlayToolBar"))
	{
		FToolMenuSection& Section = ToolbarMenu->FindOrAddSection("UExplorerBridge");
		Section.AddEntry(FToolMenuEntry::InitToolBarButton(
			"UExplorerImportQueue",
			FUIAction(FExecuteAction::CreateStatic(&FUExplorerBridgeModule::ImportAllQueuedManifests)),
			FText::FromString(TEXT("UExplorer Import")),
			FText::FromString(TEXT("Import all queued assets exported from UExplorer")),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), "Icons.Import")));

		Section.AddEntry(FToolMenuEntry::InitToolBarButton(
			"UExplorerTestElimDevice",
			FUIAction(FExecuteAction::CreateStatic(&FUExplorerShaderTranslator::RunElimDeviceTest)),
			FText::FromString(TEXT("Test ElimDevice")),
			FText::FromString(TEXT("Run built-in C++ Shader-to-Node Translator test on M_Mole_ElimDevice_Ring and NS_Mole_ElimDevice_Idle")),
			FSlateIcon(FAppStyle::GetAppStyleSetName(), "Icons.Play")));
	}
}

void FUExplorerBridgeModule::StartHttpListener()
{
	FHttpServerModule& HttpServerModule = FHttpServerModule::Get();
	TSharedPtr<IHttpRouter> Router = HttpServerModule.GetHttpRouter(BridgePort);
	if (!Router.IsValid())
	{
		UE_LOG(LogUExplorerBridge, Warning, TEXT("Failed to bind UExplorerBridge HTTP router on port %u"), BridgePort);
		return;
	}

	ImportRouteHandle = Router->BindRoute(
		FHttpPath(TEXT("/import")),
		EHttpServerRequestVerbs::VERB_POST,
		FHttpRequestHandler::CreateLambda([this](const FHttpServerRequest& Request, const FHttpResultCallback& OnComplete)
		{
			FString Body;
			FFileHelper::BufferToString(Body, Request.Body.GetData(), Request.Body.Num());

			// Defer import to FTSTicker (main loop outside TaskGraph) so UE5 Interchange
			// does not re-enter the GameThread TaskGraph queue and trip RecursionGuard.
			{
				FScopeLock Lock(&PendingManifestsMutex);
				PendingHttpManifests.Add(MoveTemp(Body));
			}

			TUniquePtr<FHttpServerResponse> Response = FHttpServerResponse::Create(TEXT("{\"status\":\"queued\"}"), TEXT("application/json"));
			OnComplete(MoveTemp(Response));
			return true;
		}));

	HttpServerModule.StartAllListeners();
	UE_LOG(LogUExplorerBridge, Log, TEXT("UExplorerBridge listening on http://127.0.0.1:%u/import"), BridgePort);
}

void FUExplorerBridgeModule::StopHttpListener()
{
	if (ImportRouteHandle.IsValid())
	{
		if (FHttpServerModule::IsAvailable())
		{
			TSharedPtr<IHttpRouter> Router = FHttpServerModule::Get().GetHttpRouter(BridgePort);
			if (Router.IsValid())
			{
				Router->UnbindRoute(ImportRouteHandle);
			}
		}
		ImportRouteHandle.Reset();
	}
}

bool FUExplorerBridgeModule::OnTickPollQueue(float DeltaTime)
{
	TArray<FString> ManifestsToProcess;
	{
		FScopeLock Lock(&PendingManifestsMutex);
		if (PendingHttpManifests.Num() > 0)
		{
			ManifestsToProcess = MoveTemp(PendingHttpManifests);
		}
	}

	for (const FString& ManifestJson : ManifestsToProcess)
	{
		FUExplorerAssetImporter::ImportManifestJson(ManifestJson);
	}

	ImportAllQueuedManifests();
	return true;
}

void FUExplorerBridgeModule::ImportAllQueuedManifests()
{
	const FString QueueDir = GetBridgeQueueDirectory();
	if (!IFileManager::Get().DirectoryExists(*QueueDir))
	{
		return;
	}

	TArray<FString> QueueFiles;
	IFileManager::Get().FindFiles(QueueFiles, *FPaths::Combine(QueueDir, TEXT("*.json")), true, false);
	if (QueueFiles.Num() == 0)
	{
		return;
	}

	QueueFiles.Sort();
	for (const FString& FileName : QueueFiles)
	{
		const FString FullPath = FPaths::Combine(QueueDir, FileName);
		UE_LOG(LogUExplorerBridge, Log, TEXT("Processing queued UExplorer manifest: %s"), *FullPath);
		const bool bOk = FUExplorerAssetImporter::ImportManifestFile(FullPath);
		if (bOk)
		{
			IFileManager::Get().Delete(*FullPath, false, true, true);
		}
	}
}

IMPLEMENT_MODULE(FUExplorerBridgeModule, UExplorerBridge)
