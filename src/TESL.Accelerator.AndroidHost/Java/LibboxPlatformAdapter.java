package com.tesl.accelerator;

import android.net.VpnService;
import android.util.Log;
import io.nekohasekai.libbox.*;

public class LibboxPlatformAdapter implements PlatformInterface {
    private static final String TAG = "LibboxPlatformAdapter";
    private final VpnService vpnService;
    private volatile int tunFd = -1;
    private OnLogListener onLogListener;

    public interface OnLogListener {
        void onLog(String message);
    }

    public LibboxPlatformAdapter(VpnService vpnService) {
        this.vpnService = vpnService;
    }

    public void setTunFd(int fd) {
        this.tunFd = fd;
    }

    public int getTunFd() {
        return this.tunFd;
    }

    public void setOnLogListener(OnLogListener listener) {
        this.onLogListener = listener;
    }

    public static synchronized void setupLibbox(android.content.Context context) {
        try {
            SetupOptions options = new SetupOptions();
            String filesDir = context.getFilesDir().getAbsolutePath();
            String cacheDir = context.getCacheDir().getAbsolutePath();
            options.setBasePath(filesDir);
            options.setWorkingPath(filesDir);
            options.setTempPath(cacheDir);
            options.setFixAndroidStack(true);
            Libbox.setup(options);
            Log.i(TAG, "Libbox.setup 成功完成，workingPath=" + filesDir);
        } catch (Throwable t) {
            Log.e(TAG, "Libbox.setup 失败", t);
        }
    }

    public static BoxService createService(String configJson, LibboxPlatformAdapter adapter) throws Exception {
        if (adapter != null && adapter.vpnService != null) {
            setupLibbox(adapter.vpnService);
        }
        return Libbox.newService(configJson, adapter);
    }

    @Override
    public int openTun(TunOptions options) {
        Log.i(TAG, "openTun handing over masterPfd fd=" + this.tunFd + " to libbox");
        if (this.tunFd < 0) {
            Log.e(TAG, "openTun error: tunFd is negative: " + this.tunFd);
            return -1;
        }
        return this.tunFd;
    }

    @Override
    public void writeLog(String message) {
        if (message == null) return;
        Log.i("sing-box", message);
        if (onLogListener != null) {
            try {
                onLogListener.onLog(message);
            } catch (Throwable ignored) {
            }
        }
    }

    @Override
    public void autoDetectInterfaceControl(int fd) {
        if (vpnService != null && fd > 0) {
            boolean ok = vpnService.protect(fd);
            if (!ok) {
                Log.w(TAG, "autoDetectInterfaceControl: vpnService.protect(fd=" + fd + ") failed");
            }
        }
    }

    @Override
    public int findConnectionOwner(int ipProtocol, String sourceAddress, int sourcePort, String destinationAddress, int destinationPort) {
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.Q && connectivityManager != null) {
            try {
                java.net.InetSocketAddress src = new java.net.InetSocketAddress(sourceAddress, sourcePort);
                java.net.InetSocketAddress dst = new java.net.InetSocketAddress(destinationAddress, destinationPort);
                return connectivityManager.getConnectionOwnerUid(ipProtocol, src, dst);
            } catch (Throwable ignored) {
            }
        }
        return -1;
    }

    private static class SimpleStringIterator implements StringIterator {
        private final java.util.List<String> list;
        private int index = 0;

        public SimpleStringIterator(java.util.List<String> list) {
            this.list = list != null ? list : java.util.Collections.<String>emptyList();
        }

        @Override
        public boolean hasNext() {
            return index < list.size();
        }

        @Override
        public int len() {
            return list.size();
        }

        @Override
        public String next() {
            if (index < list.size()) {
                return list.get(index++);
            }
            return "";
        }
    }

    private static class SimpleNetworkInterfaceIterator implements NetworkInterfaceIterator {
        private final java.util.List<io.nekohasekai.libbox.NetworkInterface> list;
        private int index = 0;

        public SimpleNetworkInterfaceIterator(java.util.List<io.nekohasekai.libbox.NetworkInterface> list) {
            this.list = list != null ? list : java.util.Collections.<io.nekohasekai.libbox.NetworkInterface>emptyList();
        }

        @Override
        public boolean hasNext() {
            return index < list.size();
        }

        @Override
        public io.nekohasekai.libbox.NetworkInterface next() {
            if (index < list.size()) {
                return list.get(index++);
            }
            return null;
        }
    }

    @Override
    public NetworkInterfaceIterator getInterfaces() {
        java.util.List<io.nekohasekai.libbox.NetworkInterface> result = new java.util.ArrayList<>();
        try {
            java.util.Enumeration<java.net.NetworkInterface> en = java.net.NetworkInterface.getNetworkInterfaces();
            if (en != null) {
                while (en.hasMoreElements()) {
                    java.net.NetworkInterface ni = en.nextElement();
                    if (ni == null) continue;
                    io.nekohasekai.libbox.NetworkInterface boxIf = new io.nekohasekai.libbox.NetworkInterface();
                    boxIf.setName(ni.getName());
                    boxIf.setIndex(ni.getIndex());
                    try {
                        boxIf.setMTU(ni.getMTU() > 0 ? ni.getMTU() : 1500);
                    } catch (Throwable ignored) {
                        boxIf.setMTU(1500);
                    }
                    java.util.List<String> addrs = new java.util.ArrayList<>();
                    for (java.net.InterfaceAddress ia : ni.getInterfaceAddresses()) {
                        if (ia != null && ia.getAddress() != null) {
                            String host = ia.getAddress().getHostAddress();
                            if (host != null) {
                                int percentIdx = host.indexOf('%');
                                if (percentIdx >= 0) {
                                    host = host.substring(0, percentIdx);
                                }
                                addrs.add(host + "/" + ia.getNetworkPrefixLength());
                            }
                        }
                    }
                    boxIf.setAddresses(new SimpleStringIterator(addrs));

                    int flags = 1; // 默认 FlagUp = 1
                    try {
                        flags = (ni.isUp() ? 1 : 0)
                              | (ni.isLoopback() ? 4 : 0)
                              | (ni.isPointToPoint() ? 8 : 0)
                              | (ni.supportsMulticast() ? 16 : 0);
                        if (flags == 0 && !addrs.isEmpty()) {
                            flags = 1;
                        }
                    } catch (Throwable ignored) {
                        flags = 1;
                    }
                    boxIf.setFlags(flags);
                    boxIf.setType(ni.getName().startsWith("wlan") ? 1 : (ni.getName().startsWith("rmnet") || ni.getName().startsWith("ccmni") ? 2 : 0));

                    java.util.List<String> dnsList = new java.util.ArrayList<>();
                    if (connectivityManager != null) {
                        try {
                            android.net.Network[] nets = connectivityManager.getAllNetworks();
                            if (nets != null) {
                                for (android.net.Network net : nets) {
                                    android.net.LinkProperties lp = connectivityManager.getLinkProperties(net);
                                    if (lp != null && ni.getName().equals(lp.getInterfaceName())) {
                                        for (java.net.InetAddress dns : lp.getDnsServers()) {
                                            String host = dns.getHostAddress();
                                            if (host != null) {
                                                int pIdx = host.indexOf('%');
                                                if (pIdx >= 0) host = host.substring(0, pIdx);
                                                dnsList.add(host);
                                            }
                                        }
                                    }
                                }
                            }
                        } catch (Throwable ignored) {}
                    }
                    if (dnsList.isEmpty()) {
                        dnsList.add("119.29.29.29");
                        dnsList.add("223.5.5.5");
                    }
                    boxIf.setDNSServer(new SimpleStringIterator(dnsList));
                    result.add(boxIf);
                }
            }
            Log.i(TAG, "getInterfaces returning " + result.size() + " interfaces");
        } catch (Throwable t) {
            Log.e(TAG, "getInterfaces error", t);
        }
        return new SimpleNetworkInterfaceIterator(result);
    }

    @Override
    public boolean includeAllNetworks() {
        return false;
    }

    @Override
    public String packageNameByUid(int uid) {
        if (vpnService != null && vpnService.getPackageManager() != null) {
            String[] names = vpnService.getPackageManager().getPackagesForUid(uid);
            if (names != null && names.length > 0) {
                return names[0];
            }
        }
        return "";
    }

    @Override
    public int uidByPackageName(String packageName) {
        return -1;
    }

    @Override
    public boolean underNetworkExtension() {
        return false;
    }

    @Override
    public boolean usePlatformAutoDetectInterfaceControl() {
        return true;
    }

    @Override
    public boolean useProcFS() {
        return false;
    }

    @Override
    public void clearDNSCache() {
    }

    private android.net.ConnectivityManager connectivityManager;
    private android.net.ConnectivityManager.NetworkCallback defaultNetworkCallback;
    private InterfaceUpdateListener currentInterfaceListener;

    private android.net.Network findPhysicalNetwork() {
        if (connectivityManager == null) return null;
        try {
            android.net.Network[] allNets = connectivityManager.getAllNetworks();
            if (allNets == null) return null;

            android.net.Network wifiNet = null;
            android.net.Network cellNet = null;
            android.net.Network otherNet = null;

            for (android.net.Network net : allNets) {
                android.net.NetworkCapabilities caps = connectivityManager.getNetworkCapabilities(net);
                if (caps == null) continue;
                if (caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)) continue;
                if (!caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN)) continue;
                if (!caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)) continue;

                if (caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI)) {
                    wifiNet = net;
                    break;
                } else if (caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_CELLULAR)) {
                    cellNet = net;
                } else {
                    otherNet = net;
                }
            }
            if (wifiNet != null) return wifiNet;
            if (cellNet != null) return cellNet;
            return otherNet;
        } catch (Throwable t) {
            Log.e(TAG, "findPhysicalNetwork error", t);
            return null;
        }
    }

    private synchronized void notifyDefaultInterface(InterfaceUpdateListener listener) {
        if (vpnService == null || listener == null) return;
        try {
            if (connectivityManager == null) {
                connectivityManager = (android.net.ConnectivityManager) vpnService.getSystemService(android.content.Context.CONNECTIVITY_SERVICE);
            }
            if (connectivityManager == null) return;

            android.net.Network targetNet = connectivityManager.getActiveNetwork();
            boolean isVpn = false;
            if (targetNet != null) {
                android.net.NetworkCapabilities caps = connectivityManager.getNetworkCapabilities(targetNet);
                if (caps != null && (caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN) || !caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN))) {
                    isVpn = true;
                } else {
                    android.net.LinkProperties lp = connectivityManager.getLinkProperties(targetNet);
                    if (lp != null && lp.getInterfaceName() != null && lp.getInterfaceName().startsWith("tun")) {
                        isVpn = true;
                    }
                }
            } else {
                isVpn = true;
            }

            if (isVpn) {
                android.net.Network physNet = findPhysicalNetwork();
                if (physNet != null) {
                    targetNet = physNet;
                }
            }

            if (targetNet == null) {
                Log.w(TAG, "notifyDefaultInterface: 未找到可用的物理出口网络");
                return;
            }

            // 告知 VpnService 当前底层物理网络
            try {
                vpnService.setUnderlyingNetworks(new android.net.Network[]{targetNet});
            } catch (Throwable t) {
                Log.w(TAG, "vpnService.setUnderlyingNetworks 警告: " + t.getMessage());
            }

            android.net.LinkProperties lp = connectivityManager.getLinkProperties(targetNet);
            if (lp == null) {
                Log.w(TAG, "notifyDefaultInterface: LinkProperties is null");
                return;
            }
            String ifName = lp.getInterfaceName();
            if (ifName == null || ifName.isEmpty() || ifName.startsWith("tun")) {
                Log.w(TAG, "notifyDefaultInterface: 仍为无效或自环接口: " + ifName);
                return;
            }

            java.net.NetworkInterface ni = java.net.NetworkInterface.getByName(ifName);
            int ifIndex = (ni != null) ? ni.getIndex() : 0;
            boolean hasV4 = false;
            boolean hasV6 = false;
            for (android.net.LinkAddress addr : lp.getLinkAddresses()) {
                if (addr.getAddress() instanceof java.net.Inet4Address) hasV4 = true;
                if (addr.getAddress() instanceof java.net.Inet6Address) hasV6 = true;
            }
            Log.i(TAG, "notifyDefaultInterface: 成功锁定物理出口网卡 ifName=" + ifName + ", index=" + ifIndex + ", v4=" + hasV4 + ", v6=" + hasV6);
            listener.updateDefaultInterface(ifName, ifIndex, hasV4, hasV6);
        } catch (Throwable t) {
            Log.e(TAG, "notifyDefaultInterface error", t);
        }
    }

    @Override
    public synchronized void startDefaultInterfaceMonitor(final InterfaceUpdateListener listener) {
        Log.i(TAG, "startDefaultInterfaceMonitor called");
        this.currentInterfaceListener = listener;
        notifyDefaultInterface(listener);

        if (vpnService != null) {
            try {
                if (connectivityManager == null) {
                    connectivityManager = (android.net.ConnectivityManager) vpnService.getSystemService(android.content.Context.CONNECTIVITY_SERVICE);
                }
                if (connectivityManager != null && defaultNetworkCallback == null) {
                    defaultNetworkCallback = new android.net.ConnectivityManager.NetworkCallback() {
                        @Override
                        public void onAvailable(android.net.Network network) {
                            Log.i(TAG, "NetworkCallback: onAvailable " + network);
                            writeLog("[NetworkCallback] 物理网络就绪: " + network);
                            notifyDefaultInterface(currentInterfaceListener);
                        }

                        @Override
                        public void onCapabilitiesChanged(android.net.Network network, android.net.NetworkCapabilities capabilities) {
                            notifyDefaultInterface(currentInterfaceListener);
                        }

                        @Override
                        public void onLinkPropertiesChanged(android.net.Network network, android.net.LinkProperties linkProperties) {
                            Log.i(TAG, "NetworkCallback: onLinkPropertiesChanged " + network);
                            writeLog("[NetworkCallback] 物理网络链路属性已变更");
                            notifyDefaultInterface(currentInterfaceListener);
                        }

                        @Override
                        public void onLost(android.net.Network network) {
                            Log.i(TAG, "NetworkCallback: onLost " + network);
                            writeLog("[NetworkCallback] 物理网络已断开: " + network);
                            notifyDefaultInterface(currentInterfaceListener);
                        }
                    };

                    android.net.NetworkRequest request = new android.net.NetworkRequest.Builder()
                            .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                            .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
                            .build();
                    connectivityManager.registerNetworkCallback(request, defaultNetworkCallback);
                }
            } catch (Throwable t) {
                Log.e(TAG, "registerNetworkCallback error", t);
            }
        }
    }

    @Override
    public synchronized void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
        Log.i(TAG, "closeDefaultInterfaceMonitor called");
        if (connectivityManager != null && defaultNetworkCallback != null) {
            try {
                connectivityManager.unregisterNetworkCallback(defaultNetworkCallback);
            } catch (Throwable ignored) {
            }
            defaultNetworkCallback = null;
        }
        this.currentInterfaceListener = null;
    }

    @Override
    public WIFIState readWIFIState() {
        return null;
    }

    @Override
    public void sendNotification(Notification notification) {
    }
}
