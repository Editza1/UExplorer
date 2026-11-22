#pragma once

#include "CoreMinimal.h"
#include "Modules/ModuleManager.h"
#include "Containers/Ticker.h"
#include "HttpRouteHandle.h"

class FUExplorerBridgeModule : public IModuleInterface
{
public:
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;

	static void ImportAllQueuedManifests();

private:
	void RegisterMenus();
	void StartHttpListener();
	void StopHttpListener();
	bool OnTickPollQueue(float DeltaTime);

	FHttpRouteHandle ImportRouteHandle;
	FTSTicker::FDelegateHandle TickerHandle;
	FCriticalSection PendingManifestsMutex;
	TArray<FString> PendingHttpManifests;
	static constexpr uint32 BridgePort = 47291;
};
