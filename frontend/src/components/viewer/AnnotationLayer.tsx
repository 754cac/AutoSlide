import React, { useRef, useState, useEffect } from 'react';

export default function AnnotationLayer({ 
  annotations, 
  tool, 
  color, 
  opacity, 
  onAddAnnotation, 
  onUpdateAnnotation, 
  onDeleteAnnotation 
}) {
  const layerRef = useRef(null);

  // Stroke drawing has been removed. This layer now only handles text annotations.

  const handleClick = (e) => {
    if (tool === 'text') {
      const point = getRelativePoint(e);
      onAddAnnotation({
        id: Date.now(),
        type: 'text',
        x: point.x,
        y: point.y,
        content: 'Double click to edit',
        color: color,
        fontSize: 16
      });
    }
  };

  const getRelativePoint = (e) => {
    const rect = layerRef.current.getBoundingClientRect();
    const w = rect.width || 1;
    const h = rect.height || 1;

    const rawX = (e.clientX - rect.left) / w * 100;
    const rawY = (e.clientY - rect.top) / h * 100;

    const x = Math.min(100, Math.max(0, Number(rawX)));
    const y = Math.min(100, Math.max(0, Number(rawY)));

    if (!Number.isFinite(x) || !Number.isFinite(y)) {
      console.warn('Invalid relative point computed', { rawX, rawY, rect });
      return { x: 0, y: 0 };
    }

    return { x, y };
  };

  // Render SVG path from points
  const renderPath = (points) => {
    if (points.length === 0) return '';
    const d = points.map((p, i) => 
      `${i === 0 ? 'M' : 'L'} ${p.x} ${p.y}`
    ).join(' ');
    return d;
  };

  return (
    <div 
      ref={layerRef}
      className={`annotation-layer ${tool}`}
      onClick={handleClick}
      style={{
        position: 'absolute',
        top: 0,
        left: 0,
        width: '100%',
        height: '100%',
        zIndex: 10,
        cursor: tool === 'text' ? 'text' : 'default',
        pointerEvents: tool === 'text' ? 'auto' : 'none'
      }}
    >
      <svg 
        width="100%" 
        height="100%" 
        viewBox="0 0 100 100" 
        preserveAspectRatio="none"
        style={{ pointerEvents: 'none' }} // Let clicks pass through SVG to div
      >
        {/* Render existing paths */}
        {annotations.filter(a => a.type === 'path').map(ann => (
          <path
            key={ann.id}
            d={renderPath(ann.points)}
            stroke={ann.color}
            strokeWidth={ann.strokeWidth / 10} // Scale stroke relative to viewBox 0-100
            fill="none"
            opacity={ann.opacity}
            strokeLinecap="round"
            strokeLinejoin="round"
            style={{ pointerEvents: 'none' }}
          />
        ))}


      </svg>

      {/* Render Text Annotations */}
      {annotations.filter(a => a.type === 'text').map(ann => (
        <div
          key={ann.id}
          style={{
            position: 'absolute',
            left: `${ann.x}%`,
            top: `${ann.y}%`,
            color: ann.color,
            fontSize: `${ann.fontSize}px`,
            pointerEvents: 'auto',
            cursor: 'text'
          }}
        >
          {tool === 'cursor' || tool === 'text' ? (
             <input 
               autoFocus={true}
               value={ann.content}
               onChange={(e) => onUpdateAnnotation(ann.id, { content: e.target.value })}
               style={{ 
                 background: 'transparent', 
                 border: '1px dashed transparent', 
                 color: 'inherit', 
                 font: 'inherit',
                 outline: 'none'
               }}
               onFocus={(e) => e.target.style.borderColor = '#ccc'}
               onBlur={(e) => e.target.style.borderColor = 'transparent'}
             />
          ) : (
            <span>{ann.content}</span>
          )}
        </div>
      ))}
    </div>
  );
}
