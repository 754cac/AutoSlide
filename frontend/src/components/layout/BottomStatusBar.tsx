import React from 'react';

export default function BottomStatusBar({ connectionState, lastUpdated, onRetry }) {
  const getRelativeTime = (timeString) => {
    if (!timeString) return 'N/A';
    return timeString; 
  };

  const isConnected = connectionState === 'connected';
  const isReconnecting = connectionState === 'reconnecting';
  const isDisconnected = connectionState === 'disconnected';

  return (
    <div className="status-bar">
      <div className={`status-item connection ${connectionState}`}>
        <span 
          className={`status-dot ${connectionState}`}
          title={isConnected ? 'Connected to live session' : 'Connection lost'}
        ></span>
        <span className="status-text">
            {isConnected ? 'Connected' : isReconnecting ? 'Reconnecting...' : 'Disconnected'}
        </span>
        {(isDisconnected || isReconnecting) && (
            <button className="retry-btn" onClick={onRetry}>Retry Now</button>
        )}
      </div>
      <div className="status-item last-updated" title={`Exact time: ${lastUpdated}`}>
        Last unlock: {getRelativeTime(lastUpdated)}
      </div>
    </div>
  );
}
