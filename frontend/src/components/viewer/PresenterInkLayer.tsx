import React, { useRef, useEffect } from 'react';

/**
 * PresenterInkLayer - Renders real-time ink strokes from the presenter on top of the slide.
 * Converts normalized coordinates (0-1) to pixel positions based on container dimensions.
 * 
 * Props:
 * - strokes: Array of stroke objects with normalized points (0-1 range)
 * - width: Width of the slide container in pixels
 * - height: Height of the slide container in pixels
 * - scale: Current zoom scale of the slide
 */
export default function PresenterInkLayer({ strokes = [], width, height, scale = 1 }) {
    const canvasRef = useRef(null);
    
    // Parse color string (handles both "#AARRGGBB" WPF format and standard hex)
    const parseColor = (colorStr) => {
        if (!colorStr) return 'rgba(255, 0, 0, 1)';
        
        // Remove # if present
        let hex = colorStr.replace('#', '');
        
        // Handle ARGB format (WPF) - e.g., "FFFF0000" for red
        if (hex.length === 8) {
            const a = parseInt(hex.substring(0, 2), 16) / 255;
            const r = parseInt(hex.substring(2, 4), 16);
            const g = parseInt(hex.substring(4, 6), 16);
            const b = parseInt(hex.substring(6, 8), 16);
            return `rgba(${r}, ${g}, ${b}, ${a})`;
        }
        
        // Standard RGB format
        if (hex.length === 6) {
            const r = parseInt(hex.substring(0, 2), 16);
            const g = parseInt(hex.substring(2, 4), 16);
            const b = parseInt(hex.substring(4, 6), 16);
            return `rgba(${r}, ${g}, ${b}, 1)`;
        }
        
        // Fallback
        return colorStr;
    };
    
    // Helper to get point coordinates - handles both array [x, y] and object {x, y} formats
    const getPointCoords = (point) => {
        if (Array.isArray(point)) {
            return { x: point[0], y: point[1] };
        }
        if (point && typeof point.x === 'number' && typeof point.y === 'number') {
            return { x: point.x, y: point.y };
        }
        console.warn('[PresenterInkLayer] Invalid point format:', point);
        return null;
    };
    
    // Redraw all strokes when strokes array changes
    useEffect(() => {
        const canvas = canvasRef.current;
        if (!canvas) {
            console.log('[PresenterInkLayer] Canvas ref not available');
            return;
        }
        if (!width || !height || width <= 0 || height <= 0) {
            console.log('[PresenterInkLayer] Invalid dimensions:', { width, height });
            return;
        }
        
        const ctx = canvas.getContext('2d');
        
        // Clear canvas
        ctx.clearRect(0, 0, width, height);
        
        console.log(`[PresenterInkLayer] Drawing ${strokes.length} strokes on canvas ${width}x${height}`);
        
        // Draw each stroke
        strokes.forEach((stroke, strokeIndex) => {
            if (!stroke.points || stroke.points.length < 2) {
                console.log(`[PresenterInkLayer] Stroke ${strokeIndex} skipped - not enough points:`, stroke.points?.length);
                return;
            }
            
            const color = parseColor(stroke.color);
            const lineWidth = (stroke.width || 3) * (scale || 1);
            
            ctx.beginPath();
            ctx.strokeStyle = color;
            ctx.lineWidth = lineWidth;
            ctx.lineCap = 'round';
            ctx.lineJoin = 'round';
            ctx.globalAlpha = stroke.opacity ?? 1;
            
            // Move to first point (denormalize from 0-1 to pixel coordinates)
            const firstPt = getPointCoords(stroke.points[0]);
            if (!firstPt) return;
            
            const firstX = firstPt.x * width;
            const firstY = firstPt.y * height;
            ctx.moveTo(firstX, firstY);
            
            console.log(`[PresenterInkLayer] Stroke ${strokeIndex}: color=${color}, width=${lineWidth}, points=${stroke.points.length}`);
            console.log(`[PresenterInkLayer]   First point: normalized(${firstPt.x.toFixed(3)}, ${firstPt.y.toFixed(3)}) -> pixel(${firstX.toFixed(1)}, ${firstY.toFixed(1)})`);
            
            // Draw lines to subsequent points
            for (let i = 1; i < stroke.points.length; i++) {
                const pt = getPointCoords(stroke.points[i]);
                if (!pt) continue;
                
                const pixelX = pt.x * width;
                const pixelY = pt.y * height;
                ctx.lineTo(pixelX, pixelY);
            }
            
            ctx.stroke();
            ctx.globalAlpha = 1;
        });
    }, [strokes, width, height, scale]);
    
    // Don't render if dimensions are invalid - but log for debugging
    if (!width || !height || width <= 0 || height <= 0) {
        console.log('[PresenterInkLayer] Not rendering - invalid dimensions:', { width, height, strokeCount: strokes.length });
        return null;
    }
    
    return (
        <canvas
            ref={canvasRef}
            width={width}
            height={height}
            style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: `${width}px`,
                height: `${height}px`,
                pointerEvents: 'none', // Click-through to PDF
                zIndex: 10
            }}
        />
    );
}
